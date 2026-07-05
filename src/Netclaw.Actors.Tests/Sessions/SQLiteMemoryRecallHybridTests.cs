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
/// Hybrid recall + absolute cosine floor coverage (memory-core-redesign Slice 4 Stage B, tasks
/// 4.1-4.5, design D6). All embedding geometry is hand-crafted (unit 2D vectors with an exact,
/// hand-computed cosine to a fixed query vector) rather than a real embedding model, so every
/// scenario is deterministic. <see cref="MemoryRecallScenarioTests"/> (the pre-Slice-4 lexical
/// gold suite, run with no embedder holder at all) is the regression proof that the degraded
/// path is untouched by this slice — this file covers the NEW hybrid behavior only.
/// </summary>
public sealed class SQLiteMemoryRecallHybridTests : IAsyncDisposable
{
    private const string ModelId = "test-recall-model";
    private const int Dimensions = 2;
    private static readonly SessionId TestSessionId = new("test-channel/hybrid-recall");

    // Query vector [1,0]. Cosines below are exact: a unit vector [cos(theta), sin(theta)] has
    // cosine similarity cos(theta) against [1,0].
    private static readonly float[] QueryVector = [1f, 0f];
    private static readonly float[] AboveFloorVector = [0.8f, 0.6f]; // cosine 0.8 (>= default floor 0.55)
    private static readonly float[] BelowFloorVector = [0.3f, 0.953939f]; // cosine 0.3 (< default floor 0.55)

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-recall-hybrid-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-07-01T00:00:00Z"));
    private readonly SQLiteMemoryStore _store;

    public SQLiteMemoryRecallHybridTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, _timeProvider);
    }

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    // ── Vector-only admission / absolute floor ──────────────────────────

    [Fact]
    public async Task Vector_only_hit_above_floor_is_injected_even_with_zero_lexical_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await SeedDocumentWithEmbeddingAsync("doc-above", "Completely unrelated title", "Body sharing no words with the query at all.", AboveFloorVector, now, ct);

        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector));
        var request = MakeRequest("zzz qqq xyz nomatch");

        var result = await coordinator.RecallAsync(request, ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-above");
    }

    [Fact]
    public async Task Vector_only_hit_below_floor_is_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await SeedDocumentWithEmbeddingAsync("doc-below", "Completely unrelated title", "Body sharing no words with the query at all.", BelowFloorVector, now, ct);

        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector));
        var request = MakeRequest("zzz qqq xyz nomatch");

        var result = await coordinator.RecallAsync(request, ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-below");
    }

    [Fact]
    public async Task Zero_candidates_above_floor_yields_empty_non_degraded_result_and_no_recall_block()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // Only a below-floor candidate exists — nothing should survive.
        await SeedDocumentWithEmbeddingAsync("doc-below", "Completely unrelated title", "Body sharing no words with the query at all.", BelowFloorVector, now, ct);

        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector));
        var request = MakeRequest("zzz qqq xyz nomatch");

        var result = await coordinator.RecallAsync(request, ct);

        Assert.False(result.Degraded);
        Assert.Empty(result.Items);

        // Session-level contract (spec: "Nothing relevant means nothing injected" — the
        // [memory-recall] block is omitted entirely, zero tokens). BuildVolatileContextBlock
        // already implements this for ANY empty, non-degraded AutomaticRecallResult — this is
        // the direct scenario test proving it holds for the hybrid zero-injection path.
        var input = new Netclaw.Actors.Sessions.ContextAssemblyInput(
            State: SessionState.Empty,
            ContextLayers: [],
            StartupContextInjected: true,
            SlashCommandSkillContent: null,
            SessionPromptOverlay: null,
            TurnRestartNotice: null,
            SessionId: TestSessionId,
            SessionsBasePath: "/tmp/netclaw-test",
            FileReadGranted: false,
            ActiveRecall: result,
            Audience: TrustAudience.Personal);

        var block = SessionMessageAssembler.BuildVolatileContextBlock(input);
        Assert.DoesNotContain("[memory-recall]", block);
    }

    // ── Policy parity (SECURITY) ─────────────────────────────────────────

    [Fact]
    public async Task Vector_sourced_secret_candidate_is_filtered_before_scoring()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await SeedDocumentWithEmbeddingAsync(
            "doc-secret", "Unrelated title", "No lexical overlap with the query.", AboveFloorVector, now, ct,
            sensitivity: "secret");

        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector));
        var request = MakeRequest("zzz qqq xyz nomatch");

        var result = await coordinator.RecallAsync(request, ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-secret");
    }

    [Fact]
    public async Task Vector_sourced_wrong_audience_candidate_is_filtered_before_scoring()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // Personal-audience document; the request below is scoped to Public, which cannot see it.
        await SeedDocumentWithEmbeddingAsync(
            "doc-personal", "Unrelated title", "No lexical overlap with the query.", AboveFloorVector, now, ct,
            audience: TrustAudience.Personal.ToWireValue());

        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector));
        var request = MakeRequest("zzz qqq xyz nomatch", audience: TrustAudience.Public);

        var result = await coordinator.RecallAsync(request, ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-personal");
    }

    [Fact]
    public async Task Vector_sourced_manual_recall_mode_candidate_is_filtered_before_scoring()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await SeedDocumentWithEmbeddingAsync(
            "doc-manual", "Unrelated title", "No lexical overlap with the query.", AboveFloorVector, now, ct,
            recallMode: "manual");

        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector));
        var request = MakeRequest("zzz qqq xyz nomatch");

        var result = await coordinator.RecallAsync(request, ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-manual");
    }

    // ── Lexical-hit cosine enrichment ────────────────────────────────────

    [Fact]
    public async Task Lexical_hit_with_a_below_floor_embedding_is_dropped_despite_strong_lexical_match()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // Strong lexical match (query terms verbatim in title/body) but a low-cosine embedding.
        // The absolute floor gates on TRUE cosine for every candidate once a query vector
        // exists, including lexical ones — a lexical hit is not exempt (task 4.3).
        await SeedDocumentWithEmbeddingAsync(
            "doc-lexical-lowcos", "Akka Stream Backpressure", "Akka stream backpressure demand signaling.",
            BelowFloorVector, now, ct);

        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector));
        var request = MakeRequest("How does Akka stream backpressure work?");

        var result = await coordinator.RecallAsync(request, ct);

        Assert.False(result.Degraded);
        Assert.DoesNotContain(result.Items, i => i.Id.Value == "doc-lexical-lowcos");
    }

    [Fact]
    public async Task Lexical_hit_with_an_above_floor_embedding_survives_and_carries_its_true_cosine_in_scoring()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await SeedDocumentWithEmbeddingAsync(
            "doc-lexical-hicos", "Akka Stream Backpressure", "Akka stream backpressure demand signaling.",
            AboveFloorVector, now, ct);

        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector));
        var request = MakeRequest("How does Akka stream backpressure work?");

        var result = await coordinator.RecallAsync(request, ct);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, i => i.Id.Value == "doc-lexical-hicos");
    }

    // ── Recency decay (floor-bounded) ────────────────────────────────────

    [Fact]
    public async Task Recency_decay_applies_the_configured_half_life_and_clamps_at_the_0_7_floor()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        const int halfLifeDays = 180;
        // 200 days old: 2^(-200/180) ≈ 0.4626, below the 0.7 clamp — expect exactly 0.7.
        var oldMs = now - (long)TimeSpan.FromDays(200).TotalMilliseconds;

        // Both docs are vector-only (no lexical overlap at all with the query), identical cosine
        // (1.0 — same vector as the query) and identical memory class/update-semantics, so the
        // ONLY difference in their composite score is the recency multiplier.
        await SeedDocumentWithEmbeddingAsync("doc-fresh", "Unrelated fresh title", "No overlap with the query.", QueryVector, now, ct);
        await SeedDocumentWithEmbeddingAsync("doc-old", "Unrelated old title", "No overlap with the query.", QueryVector, oldMs, ct);

        var recallConfig = new MemoryRecallConfig { RecencyHalfLifeDays = halfLifeDays };
        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector), recallConfig, maxItems: 5);
        var request = MakeRequest("zzz qqq xyz nomatch", maxItems: 5);

        var result = await coordinator.RecallAsync(request, ct);

        Assert.False(result.Degraded);
        var fresh = Assert.Single(result.Items, i => i.Id.Value == "doc-fresh");
        var old = Assert.Single(result.Items, i => i.Id.Value == "doc-old");

        // decay(old)/decay(fresh) == 0.7/1.0 == 0.7. Every other term in the composite (cosine,
        // squash, classPrior) is identical between the two documents, so this ratio isolates the
        // recency multiplier exactly.
        var ratio = old.Score / fresh.Score;
        Assert.Equal(0.7, ratio, precision: 3);
    }

    [Fact]
    public async Task Recency_half_life_zero_disables_decay()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var oldMs = now - (long)TimeSpan.FromDays(2000).TotalMilliseconds;

        await SeedDocumentWithEmbeddingAsync("doc-fresh", "Unrelated fresh title", "No overlap with the query.", QueryVector, now, ct);
        await SeedDocumentWithEmbeddingAsync("doc-ancient", "Unrelated ancient title", "No overlap with the query.", QueryVector, oldMs, ct);

        var recallConfig = new MemoryRecallConfig { RecencyHalfLifeDays = 0 };
        var coordinator = MakeCoordinator(new ScriptedEmbedder(QueryVector), recallConfig, maxItems: 5);
        var request = MakeRequest("zzz qqq xyz nomatch", maxItems: 5);

        var result = await coordinator.RecallAsync(request, ct);

        var fresh = Assert.Single(result.Items, i => i.Id.Value == "doc-fresh");
        var ancient = Assert.Single(result.Items, i => i.Id.Value == "doc-ancient");
        Assert.Equal(fresh.Score, ancient.Score, precision: 6);
    }

    // ── Degraded path: loud, throttled log ───────────────────────────────

    [Fact]
    public async Task Embedder_unavailable_logs_vector_degraded_once_then_throttles_within_the_cooldown_window()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // Ordinary lexical-only doc so recall has something to do while degraded.
        await SeedDocumentWithEmbeddingAsync("doc-lexical", "Akka Stream Backpressure", "Akka stream backpressure demand signaling extra token match here.", null, now, ct);

        var recordingLogger = new RecordingLogger();
        var embedderHolder = new MemoryEmbedderHolder(new UnavailableMemoryEmbedder(ModelId, "not provisioned"));
        var vectorIndexHolder = new MemoryVectorIndexHolder(_store);
        var coordinator = new SQLiteMemoryRecallCoordinator(
            _store, recordingLogger, new SessionTuning(), embedderHolder, vectorIndexHolder, new MemoryConfig());

        var request = MakeRequest("How does Akka stream backpressure work?");

        await coordinator.RecallAsync(request, ct);
        var firstCount = recordingLogger.Entries.Count(e => e.Contains("memory_recall_vector_degraded", StringComparison.Ordinal));
        Assert.Equal(1, firstCount);

        // Second call one minute later: same reason, still within the 5-minute cooldown — no new log line.
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        await coordinator.RecallAsync(request, ct);
        var secondCount = recordingLogger.Entries.Count(e => e.Contains("memory_recall_vector_degraded", StringComparison.Ordinal));
        Assert.Equal(1, secondCount);

        // Past the cooldown window: logs again.
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        await coordinator.RecallAsync(request, ct);
        var thirdCount = recordingLogger.Entries.Count(e => e.Contains("memory_recall_vector_degraded", StringComparison.Ordinal));
        Assert.Equal(2, thirdCount);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private SQLiteMemoryRecallCoordinator MakeCoordinator(IMemoryEmbedder embedder, MemoryRecallConfig? recallConfig = null, int maxItems = 3)
    {
        var embedderHolder = new MemoryEmbedderHolder(embedder);
        var vectorIndexHolder = new MemoryVectorIndexHolder(_store);
        var memoryConfig = new MemoryConfig { Recall = recallConfig ?? new MemoryRecallConfig() };
        return new SQLiteMemoryRecallCoordinator(
            _store, NullLogger<SQLiteMemoryRecallCoordinator>.Instance, new SessionTuning(), embedderHolder, vectorIndexHolder, memoryConfig);
    }

    private static AutomaticRecallRequest MakeRequest(string prompt, TrustAudience audience = TrustAudience.Public, int maxItems = 3)
        => new(
            SessionId: TestSessionId,
            Query: prompt,
            RecentUserMessages: string.IsNullOrEmpty(prompt) ? [] : [prompt],
            MaxItems: maxItems,
            Audience: audience);

    private async Task SeedDocumentWithEmbeddingAsync(
        string id,
        string title,
        string body,
        float[]? embeddingVector,
        long updatedAtMs,
        CancellationToken ct,
        string sensitivity = "normal",
        string recallMode = "auto",
        string audience = "public")
    {
        var anchor = _store.CreateDefaultAnchor(id);
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: id,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: title,
            MarkdownBody: body,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: sensitivity,
            RecallMode: recallMode,
            Confidence: 0.9,
            FreshnessAtMs: updatedAtMs,
            ExpiresAtMs: null,
            CreatedAtMs: updatedAtMs,
            UpdatedAtMs: updatedAtMs,
            Audience: audience), ct);

        if (embeddingVector is not null)
        {
            await _store.UpsertEmbeddingAsync(
                id, MemoryEmbedOnWriteCoordinator.DocumentItemKind, ModelId, contentHash: $"hash-{id}", embeddingVector, ct);
        }
    }

    /// <summary>
    /// Fake embedder that ignores its input text and always returns the same, hand-crafted
    /// query vector — mirrors <c>MemoryCurationNominatorTests.ScriptedEmbedder</c>.
    /// </summary>
    private sealed class ScriptedEmbedder(float[] queryVector) : IMemoryEmbedder
    {
        public string ModelId => SQLiteMemoryRecallHybridTests.ModelId;

        public int Dimensions => SQLiteMemoryRecallHybridTests.Dimensions;

        public bool IsAvailable => true;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct)
            => ValueTask.FromResult<ReadOnlyMemory<float>>(queryVector);

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                texts.Select(_ => (ReadOnlyMemory<float>)queryVector).ToList());
    }

    /// <summary>Records every log line emitted through the Microsoft.Extensions.Logging ctor.</summary>
    private sealed class RecordingLogger : ILogger<SQLiteMemoryRecallCoordinator>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }
}
