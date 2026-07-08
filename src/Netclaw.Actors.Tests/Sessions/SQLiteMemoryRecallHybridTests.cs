// -----------------------------------------------------------------------
// <copyright file="SQLiteMemoryRecallHybridTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Covers <see cref="SQLiteMemoryRecallCoordinator"/>'s hybrid recall path
/// (memory-core-redesign Slice 4, design D6, tasks 4.1-4.4, gap-repair fix): the absolute cosine
/// floor (embedded candidates only), the coverage-gap bypass for candidates with no embedding
/// row at all, the zero-injection contract, recency decay bounds, and degraded-path parity with
/// the pre-Slice-4 lexical-only coordinator. Fixture geometry is engineered directly via
/// hand-crafted 2D unit vectors (same technique as <c>MemoryCurationNominatorTests</c>) rather
/// than a real embedding model, so every scenario is exact and deterministic.
///
/// <para>
/// Gated-hydration policy-gate exclusions (recall_mode/boundary/audience/sensitivity/
/// memory_class) live in <c>SQLiteMemoryStoreEmbeddingTests</c> — those exercise
/// <see cref="SQLiteMemoryStore.GetRecallCandidatesByIdsAsync"/> directly, which this class does
/// not need to re-prove.
/// </para>
/// </summary>
public sealed class SQLiteMemoryRecallHybridTests : IAsyncDisposable
{
    private const string ModelId = "hybrid-recall-test-model";
    private const int Dimensions = 2;

    // A unit vector and its exact opposite: cosine(QueryVector, QueryVector) == 1.0,
    // cosine(QueryVector, OrthogonalVector) == 0.0. Sufficient geometry for every scenario here
    // (either "matches the query" or "shares no direction with it at all").
    private static readonly float[] QueryVector = [1f, 0f];
    private static readonly float[] OrthogonalVector = [0f, 1f];

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-recall-hybrid-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public SQLiteMemoryRecallHybridTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    // ── Absolute cosine floor (task 4.3; gap-repair fix case 2) ─────────

    [Fact]
    public async Task Absolute_floor_excludes_a_lexically_strong_candidate_whose_cosine_is_below_threshold()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        // Strong lexical match: title+content share every query term, so the pre-Slice-4
        // selector score alone clears the old lexical floor comfortably. Its embedding IS
        // present (case 2, not a coverage gap) but points the exact opposite direction of the
        // query vector (cosine 0.0) -- well below MinCosineSimilarity's default 0.68. The
        // absolute floor must reject an embedded-but-dissimilar candidate regardless of how
        // strong the lexical match is; only a genuine coverage gap (no embedding row at all)
        // bypasses the floor -- see the coverage-gap facts below.
        await SeedDocumentAsync("doc-lexical-strong", "Grafana dashboard provisioning convention",
            "Grafana dashboard provisioning convention details for the ops team.", ct);
        await _store.UpsertEmbeddingAsync(
            "doc-lexical-strong", MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId, "hash-orthogonal", OrthogonalVector, ct);

        var coordinator = BuildHybridCoordinator(TimeProvider.System, NullLogger<SQLiteMemoryRecallCoordinator>.Instance);

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/floor-1",
            Query: "what is our grafana dashboard provisioning convention?",
            RecentUserMessages: ["what is our grafana dashboard provisioning convention?"],
            MaxItems: 3), ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-lexical-strong");
    }

    [Fact]
    public async Task Absolute_floor_admits_a_candidate_at_or_above_the_configured_cosine_threshold()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        await SeedDocumentAsync("doc-cosine-match", "Grafana dashboard provisioning convention",
            "Grafana dashboard provisioning convention details for the ops team.", ct);
        await _store.UpsertEmbeddingAsync(
            "doc-cosine-match", MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId, "hash-match", QueryVector, ct);

        var coordinator = BuildHybridCoordinator(TimeProvider.System, NullLogger<SQLiteMemoryRecallCoordinator>.Instance);

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/floor-2",
            Query: "what is our grafana dashboard provisioning convention?",
            RecentUserMessages: ["what is our grafana dashboard provisioning convention?"],
            MaxItems: 3), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-cosine-match");
    }

    // ── Coverage gap (gap-repair fix, cases 3 and its logging) ──────────

    [Fact]
    public async Task CoverageGap_unembedded_candidate_with_a_strong_lexical_match_is_recalled()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        // No UpsertEmbeddingAsync call at all for this document -- a genuine coverage gap, not a
        // candidate the index scored and rejected. The absolute floor cannot apply to a
        // similarity that was never computed, so the gap-repair fix admits it on its lexical/
        // fused score alone, exactly as the pre-Slice-4 lexical-only path would have.
        await SeedDocumentAsync("doc-coverage-gap", "Grafana dashboard provisioning convention",
            "Grafana dashboard provisioning convention details for the ops team.", ct);

        var coordinator = BuildHybridCoordinator(TimeProvider.System, NullLogger<SQLiteMemoryRecallCoordinator>.Instance);

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/coverage-gap",
            Query: "what is our grafana dashboard provisioning convention?",
            RecentUserMessages: ["what is our grafana dashboard provisioning convention?"],
            MaxItems: 3), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-coverage-gap");
    }

    [Fact]
    public async Task CoverageGap_emits_a_rate_limited_warning_log_when_embeddings_are_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        await SeedDocumentAsync("doc-coverage-gap-log", "Grafana dashboard provisioning convention",
            "Grafana dashboard provisioning convention details for the ops team.", ct);

        var recordingLogger = new RecordingLogger<SQLiteMemoryRecallCoordinator>();
        var coordinator = new SQLiteMemoryRecallCoordinator(
            _store,
            recordingLogger,
            new MemoryConfig { Embeddings = new MemoryEmbeddingsConfig { Enabled = true } },
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true },
            embedderHolder: new MemoryEmbedderHolder(new ScriptedEmbedder(ModelId, Dimensions, QueryVector)),
            vectorIndexHolder: new MemoryVectorIndexHolder(_store));

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/coverage-gap-log",
            Query: "what is our grafana dashboard provisioning convention?",
            RecentUserMessages: ["what is our grafana dashboard provisioning convention?"],
            MaxItems: 3), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-coverage-gap-log");
        Assert.Contains(recordingLogger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("memory_recall_coverage_gap"));
    }

    [Fact]
    public async Task CoverageGap_no_log_is_emitted_when_the_corpus_is_fully_embedded()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        // Every candidate this query can surface has an embedding row (whether or not its
        // cosine clears the floor) -- no coverage gap exists, so the coverage-gap log must never
        // fire, only the ordinary absolute-floor admit/reject logic.
        await SeedDocumentAsync("doc-fully-covered-admit", "Grafana dashboard provisioning convention",
            "Grafana dashboard provisioning convention details for the ops team.", ct);
        await _store.UpsertEmbeddingAsync(
            "doc-fully-covered-admit", MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId, "hash-admit", QueryVector, ct);
        await SeedDocumentAsync("doc-fully-covered-reject", "Grafana dashboard provisioning convention",
            "Grafana dashboard provisioning convention details for the ops team, second copy.", ct);
        await _store.UpsertEmbeddingAsync(
            "doc-fully-covered-reject", MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId, "hash-reject", OrthogonalVector, ct);

        var recordingLogger = new RecordingLogger<SQLiteMemoryRecallCoordinator>();
        var coordinator = new SQLiteMemoryRecallCoordinator(
            _store,
            recordingLogger,
            new MemoryConfig { Embeddings = new MemoryEmbeddingsConfig { Enabled = true } },
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true },
            embedderHolder: new MemoryEmbedderHolder(new ScriptedEmbedder(ModelId, Dimensions, QueryVector)),
            vectorIndexHolder: new MemoryVectorIndexHolder(_store));

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/no-coverage-gap",
            Query: "what is our grafana dashboard provisioning convention?",
            RecentUserMessages: ["what is our grafana dashboard provisioning convention?"],
            MaxItems: 3), ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-fully-covered-admit");
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-fully-covered-reject");
        Assert.DoesNotContain(recordingLogger.Entries, e => e.Message.Contains("memory_recall_coverage_gap"));
    }

    // ── Zero-injection contract (task 4.3) ──────────────────────────────

    [Fact]
    public async Task Zero_survivors_returns_a_healthy_empty_result_not_a_degraded_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        // Lexically matchable AND embedded (case 2, not a coverage gap), but pointing the exact
        // opposite direction of the query vector -- cosine 0.0, well below the floor. Since the
        // gap-repair fix only bypasses the floor for a genuine coverage gap, this candidate is
        // still excluded, so this remains the "nothing relevant exists" case design D6 requires
        // to surface as healthy-empty, not a degraded/error result.
        await SeedDocumentAsync("doc-embedded-below-floor", "Grafana dashboard provisioning convention",
            "Grafana dashboard provisioning convention details for the ops team.", ct);
        await _store.UpsertEmbeddingAsync(
            "doc-embedded-below-floor", MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId, "hash-zero-survivors", OrthogonalVector, ct);

        var coordinator = BuildHybridCoordinator(TimeProvider.System, NullLogger<SQLiteMemoryRecallCoordinator>.Instance);

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/zero-survivors",
            Query: "what is our grafana dashboard provisioning convention?",
            RecentUserMessages: ["what is our grafana dashboard provisioning convention?"],
            MaxItems: 3), ct);

        Assert.False(result.Degraded);
        Assert.Empty(result.Items);
    }

    // ── Recency decay bounds (task 4.4) ─────────────────────────────────

    [Fact]
    public async Task Recency_decay_downweights_an_old_candidate_toward_the_085_floor_without_zeroing_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var fakeTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        var nowMs = fakeTime.GetUtcNow().ToUnixTimeMilliseconds();
        // Default RecencyHalfLifeDays is 30; 3650 days (10 years) drives the decay term to
        // effectively zero, isolating the 0.85 floor.
        var ancientMs = fakeTime.GetUtcNow().AddDays(-3650).ToUnixTimeMilliseconds();

        // Identical title/content/class/semantics/embedding -- every fusion component except
        // recency is equal, so any score difference is attributable to the decay multiplier
        // alone.
        await SeedDocumentAsync("doc-fresh", "Widget rollout plan", "Widget rollout plan details for the release team.", ct, updatedAtMs: nowMs);
        await SeedDocumentAsync("doc-ancient", "Widget rollout plan", "Widget rollout plan details for the release team.", ct, updatedAtMs: ancientMs);
        await _store.UpsertEmbeddingAsync("doc-fresh", MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId, "hash-fresh", QueryVector, ct);
        await _store.UpsertEmbeddingAsync("doc-ancient", MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId, "hash-ancient", QueryVector, ct);

        var coordinator = BuildHybridCoordinator(fakeTime, NullLogger<SQLiteMemoryRecallCoordinator>.Instance);

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/recency",
            Query: "widget rollout plan",
            RecentUserMessages: ["widget rollout plan"],
            MaxItems: 5), ct);

        Assert.False(result.Degraded);
        var fresh = Assert.Single(result.Items, i => i.Id.Value == "doc-fresh");
        var ancient = Assert.Single(result.Items, i => i.Id.Value == "doc-ancient");

        Assert.True(fresh.Score > ancient.Score, $"expected fresh ({fresh.Score:F6}) > ancient ({ancient.Score:F6})");

        // Fresh multiplier == 1.0 (age 0), ancient multiplier -> 0.85 floor (age >> half-life),
        // so the ratio should land within a tight tolerance of 1.0/0.85, never below it (the
        // floor guarantees the ancient candidate is downweighted by at most ~15%).
        var ratio = fresh.Score / ancient.Score;
        Assert.True(Math.Abs(ratio - (1.0 / 0.85)) < 0.01, $"expected ratio near {1.0 / 0.85:F6}, got {ratio:F6}");
    }

    // ── Degraded-path parity (task 4.1) ─────────────────────────────────

    [Fact]
    public async Task Unavailable_embedder_produces_identical_results_to_a_coordinator_built_without_holders()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        await SeedDocumentAsync("doc-degraded-parity", "TextForge Pricing Model",
            "TextForge uses a monthly subscription with a discounted annual plan.", ct,
            aliasesJson: "[\"textforge\",\"pricing model\"]");

        var request = new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/degraded-parity",
            Query: "What's the pricing model for TextForge?",
            RecentUserMessages: ["What's the pricing model for TextForge?"],
            MaxItems: 3);

        var withoutHolders = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true });

        var withUnavailableEmbedder = new SQLiteMemoryRecallCoordinator(
            _store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true },
            embedderHolder: new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "test: never provisioned")),
            vectorIndexHolder: new MemoryVectorIndexHolder(_store));

        var baselineResult = await withoutHolders.RecallAsync(request, ct);
        var degradedResult = await withUnavailableEmbedder.RecallAsync(request, ct);

        Assert.False(baselineResult.Degraded);
        Assert.False(degradedResult.Degraded);
        Assert.Equal(
            baselineResult.Items.Select(i => (i.Id.Value, i.Title, i.Content, i.Sensitivity, i.Score)),
            degradedResult.Items.Select(i => (i.Id.Value, i.Title, i.Content, i.Sensitivity, i.Score)));
    }

    // ── Rate-limited degraded log, Debug vs Warning (task 4.1) ──────────

    [Fact]
    public async Task Vector_degraded_log_is_debug_when_embeddings_are_disabled_by_config()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var recordingLogger = new RecordingLogger<SQLiteMemoryRecallCoordinator>();
        var coordinator = new SQLiteMemoryRecallCoordinator(
            _store,
            recordingLogger,
            new MemoryConfig { Embeddings = new MemoryEmbeddingsConfig { Enabled = false } },
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true },
            embedderHolder: new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "warmup has not completed yet")),
            vectorIndexHolder: new MemoryVectorIndexHolder(_store));

        await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/loglevel-debug",
            Query: "anything",
            RecentUserMessages: ["anything"],
            MaxItems: 3), ct);

        Assert.Contains(recordingLogger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("memory_recall_vector_degraded"));
        Assert.DoesNotContain(recordingLogger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("memory_recall_vector_degraded"));
    }

    [Fact]
    public async Task Vector_degraded_log_is_warning_when_embeddings_are_enabled_but_the_turn_still_degraded()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var recordingLogger = new RecordingLogger<SQLiteMemoryRecallCoordinator>();
        var coordinator = new SQLiteMemoryRecallCoordinator(
            _store,
            recordingLogger,
            new MemoryConfig { Embeddings = new MemoryEmbeddingsConfig { Enabled = true } },
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true },
            embedderHolder: new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "model load failed")),
            vectorIndexHolder: new MemoryVectorIndexHolder(_store));

        await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"hybrid/loglevel-warning",
            Query: "anything",
            RecentUserMessages: ["anything"],
            MaxItems: 3), ct);

        Assert.Contains(recordingLogger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("memory_recall_vector_degraded"));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private SQLiteMemoryRecallCoordinator BuildHybridCoordinator(TimeProvider timeProvider, ILogger<SQLiteMemoryRecallCoordinator> logger)
        => new(
            _store,
            logger,
            new MemoryConfig(),
            timeProvider,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true },
            embedderHolder: new MemoryEmbedderHolder(new ScriptedEmbedder(ModelId, Dimensions, QueryVector)),
            vectorIndexHolder: new MemoryVectorIndexHolder(_store));

    private async Task SeedDocumentAsync(
        string documentId, string title, string content, CancellationToken ct,
        long? updatedAtMs = null, string? aliasesJson = null)
    {
        var anchor = _store.CreateDefaultAnchor(documentId);
        var now = updatedAtMs ?? TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: documentId,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: title,
            MarkdownBody: content,
            AliasesJson: aliasesJson,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), ct);
    }

    /// <summary>
    /// Fake embedder that ignores its input text and always returns the same, hand-crafted query
    /// vector -- sufficient here because every test in this file embeds at most one query and
    /// the geometry (not the input text) is what needs to be controlled. Mirrors
    /// <c>MemoryCurationNominatorTests.ScriptedEmbedder</c> (kept as a separate private copy per
    /// that file's own convention).
    /// </summary>
    private sealed class ScriptedEmbedder(string modelId, int dimensions, float[] queryVector) : IMemoryEmbedder
    {
        public string ModelId => modelId;

        public int Dimensions => dimensions;

        public bool IsAvailable => true;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct)
            => ValueTask.FromResult<ReadOnlyMemory<float>>(queryVector);

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                texts.Select(_ => (ReadOnlyMemory<float>)queryVector).ToList());
    }

    /// <summary>Records every (level, message) pair logged through the generic ILogger ctor seam.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
