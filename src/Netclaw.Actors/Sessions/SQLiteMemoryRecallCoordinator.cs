// -----------------------------------------------------------------------
// <copyright file="SQLiteMemoryRecallCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Netclaw.Actors.Memory;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Automatic recall coordinator over SQLite-backed durable memory.
///
/// <para>
/// <b>Hybrid recall (memory-core-redesign Slice 4, design D6):</b> when
/// <c>embedderHolder</c>'s current embedder is available and <c>vectorIndexHolder</c> is
/// wired, each turn embeds the query once — under a fixed <see cref="VectorEmbedSubBudgetMs"/>
/// sub-budget nested inside the caller's overall <c>Memory.RecallTimeoutMs</c> via a linked
/// CTS — and unions FTS5 lexical candidates with the vector index's top-k cosine matches.
/// Vector-only hits are hydrated through <see cref="SQLiteMemoryStore.GetRecallCandidatesByIdsAsync"/>,
/// which applies the IDENTICAL policy predicates <see cref="SQLiteMemoryStore.SearchByPlanAsync"/>
/// applies to lexical hits — a vector hit can never bypass a gate a lexical one would have to
/// clear. Scoring fuses a weighted cosine + squashed lexical-selector-score + dampened
/// class-prior composite, recency-decayed, then applies an ABSOLUTE floor: any candidate
/// (regardless of source) whose cosine falls below <see cref="MemoryRecallConfig.MinCosineSimilarity"/>
/// is dropped before ranking. Zero survivors means zero injection and a HEALTHY (non-degraded)
/// empty result — the caller (<see cref="SessionMessageAssembler.BuildVolatileContextBlock"/>)
/// already omits the <c>[memory-recall]</c> block entirely for that shape.
/// </para>
///
/// <para>
/// <b>Degraded path (embedder unavailable, over its sub-budget, or no holder wired):</b> recall
/// falls back to the pre-Slice-4 lexical-only pipeline VERBATIM — same selector scoring, same
/// composite formula, same <see cref="DefaultMinimumRecallCompositeScore"/> floor — which is
/// exactly what <c>MemoryRecallScenarioTests</c> exercises and pins (constructed without either
/// holder). A rate-limited <c>memory_recall_vector_degraded</c> log fires on every fallback
/// reason: Debug when embeddings are disabled by config (the default, intentional state —
/// mirrors <c>MemoryCurationEvaluator</c>'s <c>curation_nominator_degraded</c> level choice, so
/// this is not Warning-level spam on every turn of a deployment that simply hasn't turned
/// embeddings on), Warning when embeddings are enabled but the turn still degraded (a genuine
/// runtime anomaly worth noticing: timeout, embed failure, missing index).
/// </para>
/// </summary>
public sealed class SQLiteMemoryRecallCoordinator(
    SQLiteMemoryStore store,
    ILogger<SQLiteMemoryRecallCoordinator> logger,
    MemoryConfig memoryConfig,
    TimeProvider timeProvider,
    SessionTuning? sessionTuning = null,
    MemoryEmbedderHolder? embedderHolder = null,
    MemoryVectorIndexHolder? vectorIndexHolder = null) : IMemoryRecallCoordinator
{
    private readonly SessionTuning _sessionTuning = sessionTuning ?? new SessionTuning();
    private readonly MemoryRecallConfig _recallConfig = memoryConfig.Recall;

    // Read once at construction (DI-resolved MemoryConfig is effectively immutable for the
    // process's lifetime — an operator flip requires a restart, same as every other Memory.*
    // setting). Drives the Debug-vs-Warning split on the degraded log: see this class's summary.
    private readonly bool _embeddingsEnabledByConfig = memoryConfig.Embeddings.Enabled;

    private readonly DeterministicRetrievalRequestPlanner _deterministicPlanner = new();
    private readonly DeterministicCandidateSelector _candidateSelector = new();
    private readonly ConcurrentDictionary<string, long> _lastVectorDegradedLogMs = new(StringComparer.Ordinal);

    /// <summary>
    /// Default minimum composite score a candidate must reach to survive
    /// recall. Calibrated against the new score shape (DurableFact RecallRank
    /// bonus 480 → +4.8 composite, demoted anchor/soft-scope weights) so that
    /// a durable fact needs at least two independent lexical matches
    /// (selector ~9 + class prior ~5.6 = ~14.6) or one lexical match plus a
    /// facet match to clear the floor, while a single-token collision
    /// (selector ~5, composite ~10.6) is rejected. Returning ZERO items when
    /// nothing clears the floor is intended behavior: the July 2026 audit
    /// measured that on 65% of real queries nothing relevant existed to
    /// inject. The <see cref="MemoryRecallScenarioTests"/> gold suite pins
    /// the admit side (pointed two-term questions must still recall); the
    /// audit floor sweep pins the reject side. Override via
    /// <see cref="SessionTuning.MinimumRecallCompositeScore"/>. See issue
    /// #582 and docs/research/memory-audit-2026-07.md.
    ///
    /// <para>
    /// This floor governs the DEGRADED (lexical-only) path exclusively
    /// (memory-core-redesign Slice 4). When a query vector is available the absolute cosine
    /// floor (<see cref="MemoryRecallConfig.MinCosineSimilarity"/>) governs admission instead —
    /// the two floors are never both applied to the same candidate set.
    /// </para>
    /// </summary>
    private const double DefaultMinimumRecallCompositeScore = 14.0;

    // RecallRank dampened by 100x so it acts as a tiebreaker (~2 points
    // for DurableFact+MergeDocument) rather than overriding SelectorScore
    // (~4 points per lexical match). Unchanged by Slice 4 — this constant governs the
    // degraded/lexical composite exclusively; hybrid fusion applies its own further-dampened
    // variant (see HybridClassPriorDampeningFactor) sized for a [0,1]-scale formula.
    private const double RecallRankDampeningFactor = 100.0;

    /// <summary>
    /// Sub-budget, in milliseconds, for the per-turn query embedding call
    /// (memory-core-redesign Slice 4, design D6), applied via a CTS linked to (nested inside)
    /// the caller's overall recall <c>ct</c> (<c>Memory.RecallTimeoutMs</c>, default 300ms).
    /// Not a config knob: design D6 measured dynamic-length embedding (Slice 4 Stage A,
    /// <c>tools/embed-latency-bench</c>) at short-query p50 ≈ 19ms / p95 ≈ 21ms on the
    /// reference box, so 150ms leaves roughly 7x headroom over that measurement before a
    /// moderately loaded host would flap into the degraded path on every turn — a deliberately
    /// generous, fixed ceiling rather than a value operators should be tempted to tune per
    /// environment.
    /// </summary>
    private const int VectorEmbedSubBudgetMs = 150;

    /// <summary>
    /// Number of nearest-neighbor vector candidates fetched per recall turn (design D6). Sized
    /// well above <c>Memory.AutoRecallMaxItems</c> since the union with lexical candidates and
    /// the absolute cosine floor both shrink the pool before the outer MaxItems/char-budget
    /// bounds apply.
    /// </summary>
    private const int VectorTopK = 50;

    /// <summary>
    /// Minimum interval between two <c>memory_recall_vector_degraded</c> log lines for the SAME
    /// degradation reason, so a long-lived degraded condition (embeddings disabled, model
    /// unprovisioned) does not log on every single turn.
    /// </summary>
    private static readonly TimeSpan VectorDegradedLogCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hybrid fusion dampens the class prior further than the lexical/degraded path: cosine
    /// (0..1) and squash(selectorScore) (0..~1) are both already bounded fusion terms, so
    /// applying only the lexical path's /100 dampening (max ≈ 4.8 for DurableFact+MergeDocument)
    /// would let the class prior swamp both fusion terms instead of acting as a tiebreaker the
    /// way it does against an unbounded SelectorScore. Dividing the already-/100-dampened prior
    /// by a further 10x caps it at ≈0.48 — comparable in magnitude to, but never dominant over,
    /// VectorWeight*cosine or LexicalWeight*squash(selectorScore).
    /// </summary>
    private const double HybridClassPriorDampeningFactor = 10.0;

    /// <summary>
    /// Half-saturation constant for <c>squash(s) = s / (s + SquashHalfSaturation)</c>, which maps
    /// <see cref="DeterministicCandidateSelector"/>'s unbounded selector score (baseline 1.0,
    /// +4/lexical term, +6/facet, +2/anchor) into [0, 1) for hybrid fusion. At 8.0: a single
    /// lexical-term collision (score ≈5) squashes to ≈0.38, two independent matches (score ≈9)
    /// to ≈0.53, and a facet-boosted match (score ≈15) to ≈0.65 — so lexical evidence
    /// meaningfully moves the fused score without a bare baseline (score 1.0 → squash ≈0.11,
    /// i.e. no real lexical evidence at all) competing with genuine vector similarity.
    /// </summary>
    private const double SquashHalfSaturation = 8.0;

    /// <summary>
    /// Recency-decay floor for the hybrid fusion multiplier (task 4.4):
    /// <c>0.85 + 0.15 * 2^(-ageDays/RecencyHalfLifeDays)</c>. Structurally bounded in
    /// (0.85, 1.0] for any non-negative age (the decay term is always in (0, 1]), so recency can
    /// only break a tie between otherwise-similar matches, never suppress an old-but-strong
    /// match by more than 15%.
    /// </summary>
    private const double RecencyDecayFloor = 0.85;

    private const double RecencyDecayRange = 0.15;

    public async Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
    {
        try
        {
            if (_sessionTuning.DeterministicRetrievalEnabled)
            {
                DeterministicRetrievalRequestPlan deterministicPlan;
                try
                {
                    deterministicPlan = _deterministicPlanner.Plan(request);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "memory_recall_degraded session={SessionId} stage=planning reason={Reason}", request.SessionId, ex.Message);
                    return new AutomaticRecallResult([], true, ex.Message, "planning");
                }

                logger.LogInformation(
                    "memory_retrieval_request_plan session={SessionId} mode={Mode} candidateLimit={CandidateLimit} facets={Facets} softScopes={SoftScopes} anchorHints={AnchorHints} lexicalTerms={LexicalTerms}",
                    request.SessionId,
                    deterministicPlan.RetrievalMode,
                    deterministicPlan.CandidateLimit,
                    string.Join("|", deterministicPlan.Facets),
                    string.Join("|", deterministicPlan.SoftScopes),
                    string.Join("|", deterministicPlan.AnchorHints),
                    string.Join("|", deterministicPlan.LexicalTerms));

                var effectiveBoundary = Memory.MemoryPolicyScopeResolver.ResolveBoundary(request.Boundary);

                var rawCandidates = await store.SearchByPlanAsync(
                    deterministicPlan.LexicalTerms.Count > 0 ? deterministicPlan.LexicalTerms : [request.Query],
                    deterministicPlan.AllowedMemoryClasses,
                    deterministicPlan.CandidateLimit,
                    effectiveBoundary,
                    request.Audience,
                    allowExpiredEvidence: false,
                    ct);

                var scoredCandidates = _candidateSelector.SelectWithScores(deterministicPlan, rawCandidates);
                logger.LogInformation(
                    "memory_retrieval_candidate_selection session={SessionId} rawCount={RawCount} selectedCount={SelectedCount} scored={Scored}",
                    request.SessionId,
                    rawCandidates.Count,
                    scoredCandidates.Count,
                    string.Join("|", scoredCandidates.Select(x => $"{x.Item.Id}={x.SelectorScore:F1}")));

                var deterministicMaxItems = request.MaxItems <= 0 ? 3 : request.MaxItems;
                var minimumCompositeScore = _sessionTuning.MinimumRecallCompositeScore ?? DefaultMinimumRecallCompositeScore;

                string mode;
                RankedCandidate[] aboveFloor;
                int totalConsidered;

                // ── Vector query embedding (memory-core-redesign Slice 4, task 4.1) ──
                // Attempted once per turn, sub-budgeted inside the caller's overall ct. ANY
                // failure here (unavailable, missing index, sub-budget timeout, embed error)
                // degrades to the lexical-only path below, logged but never throws.
                var embedded = await TryEmbedQueryAsync(request, ct);

                if (embedded is { } hybridInput)
                {
                    mode = "hybrid";
                    (aboveFloor, totalConsidered) = await ScoreHybrid(
                        request, deterministicPlan, effectiveBoundary, scoredCandidates, hybridInput, ct);
                }
                else
                {
                    mode = "lexical";
                    var rankedCandidates = scoredCandidates
                        .Select(x => new RankedCandidate(
                            x.Item,
                            x.SelectorScore + (RecallRank(x.Item) / RecallRankDampeningFactor),
                            Cosine: null))
                        .OrderByDescending(x => x.Composite)
                        .ToArray();

                    totalConsidered = rankedCandidates.Length;
                    aboveFloor = rankedCandidates
                        .Where(x => x.Composite >= minimumCompositeScore)
                        .ToArray();
                }

                // Char budget: admit items in rank order until the next item's
                // content would blow the per-turn budget. Whole items are
                // dropped, never truncated — a truncated memory reads as
                // complete while missing its distinguishing detail.
                var charBudget = _sessionTuning.MaxRecallInjectedChars;
                var injectedChars = 0;
                var droppedByBudget = 0;
                var budgeted = new List<AutomaticRecallItem>(deterministicMaxItems);
                foreach (var x in aboveFloor)
                {
                    if (budgeted.Count >= deterministicMaxItems)
                        break;
                    var content = x.Item.Content ?? string.Empty;
                    if (charBudget > 0 && budgeted.Count > 0 && injectedChars + content.Length > charBudget)
                    {
                        droppedByBudget++;
                        continue;
                    }

                    injectedChars += content.Length;
                    budgeted.Add(new AutomaticRecallItem(
                        x.Item.Id,
                        x.Item.Title,
                        content,
                        x.Item.Sensitivity,
                        x.Composite));
                }

                var deterministicItems = budgeted.ToArray();

                logger.LogInformation(
                    "memory_retrieval_final session={SessionId} mode={Mode} injectedCount={InjectedCount} filteredByFloor={FilteredByFloor} appliedFloor={AppliedFloor:F3} injectedChars={InjectedChars} droppedByBudget={DroppedByBudget} items={Items}",
                    request.SessionId,
                    mode,
                    deterministicItems.Length,
                    totalConsidered - aboveFloor.Length,
                    mode == "hybrid" ? _recallConfig.MinCosineSimilarity : minimumCompositeScore,
                    injectedChars,
                    droppedByBudget,
                    string.Join("|", deterministicItems.Select(i => $"{i.Id.Value}=score{i.Score:F3}")));

                logger.LogDebug(
                    "memory_retrieval_final_detail session={SessionId} items={Items}",
                    request.SessionId,
                    string.Join("|", deterministicItems.Select(i => $"{i.Id.Value}={i.Title}")));

                return new AutomaticRecallResult(deterministicItems);
            }

            // Deterministic retrieval is the only path. If it's disabled,
            // return nothing rather than falling back to a dead LLM sidecar
            // path. Callers that want zero recall should just not construct
            // a coordinator or set DeterministicRetrievalEnabled = false.
            return new AutomaticRecallResult([]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "memory_recall_degraded session={SessionId} stage=execution reason={Reason}", request.SessionId, ex.Message);
            return new AutomaticRecallResult([], true, ex.Message, "execution");
        }
    }

    /// <summary>
    /// Attempts to embed <paramref name="request"/>'s query for hybrid recall
    /// (memory-core-redesign Slice 4, task 4.1). Returns null — logging the specific
    /// degradation reason via <see cref="LogVectorDegraded"/> — for every failure mode:
    /// no embedder wired, embedder unavailable, no vector index wired, index reload failure,
    /// sub-budget timeout, or an embedding call exception. Never throws; callers treat null as
    /// "run the lexical-only path," identically regardless of which reason produced it.
    /// </summary>
    private async Task<(ReadOnlyMemory<float> QueryVector, MemoryVectorIndex Index)?> TryEmbedQueryAsync(
        AutomaticRecallRequest request, CancellationToken ct)
    {
        var embedder = embedderHolder?.Current;
        if (embedder is null)
        {
            LogVectorDegraded(request.SessionId.Value, "no_embedder_configured");
            return null;
        }

        if (!embedder.IsAvailable)
        {
            LogVectorDegraded(request.SessionId.Value, "embedder_unavailable");
            return null;
        }

        if (vectorIndexHolder is null)
        {
            LogVectorDegraded(request.SessionId.Value, "no_vector_index_configured");
            return null;
        }

        MemoryVectorIndex? index;
        try
        {
            index = await vectorIndexHolder.GetCurrentAsync(embedder, ct);
        }
        catch (Exception ex)
        {
            LogVectorDegraded(request.SessionId.Value, $"vector_index_reload_failed:{ex.GetType().Name}");
            return null;
        }

        if (index is null)
        {
            LogVectorDegraded(request.SessionId.Value, "vector_index_unavailable");
            return null;
        }

        try
        {
            using var vectorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            vectorCts.CancelAfter(VectorEmbedSubBudgetMs);
            var vector = await embedder.EmbedAsync(request.Query, vectorCts.Token);
            return (vector, index);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The sub-budget's own timer fired, not the caller's outer recall ct — degrade to
            // lexical rather than propagating a cancellation that would fail the whole turn.
            LogVectorDegraded(request.SessionId.Value, "sub_budget_exceeded");
            return null;
        }
        catch (Exception ex)
        {
            LogVectorDegraded(request.SessionId.Value, $"embed_failed:{ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Builds the hybrid-mode ranked candidate pool (memory-core-redesign Slice 4, tasks
    /// 4.2-4.4): vector top-k unioned with the lexical candidates already selected against the
    /// plan, fused per design D6's weighted formula, recency-decayed, then filtered to the
    /// absolute cosine floor. Vector-only ids are hydrated through
    /// <see cref="SQLiteMemoryStore.GetRecallCandidatesByIdsAsync"/> — the SAME policy gates
    /// <see cref="SQLiteMemoryStore.SearchByPlanAsync"/> applied to the lexical candidates — and
    /// scored via <see cref="DeterministicCandidateSelector.Score"/> so a vector hit that also
    /// happens to match plan terms is not scored as if it had none.
    /// </summary>
    private async Task<(RankedCandidate[] AboveFloor, int TotalConsidered)> ScoreHybrid(
        AutomaticRecallRequest request,
        DeterministicRetrievalRequestPlan deterministicPlan,
        string effectiveBoundary,
        IReadOnlyList<DeterministicCandidateSelector.ScoredCandidate> scoredCandidates,
        (ReadOnlyMemory<float> QueryVector, MemoryVectorIndex Index) hybridInput,
        CancellationToken ct)
    {
        var (queryVector, vectorIndex) = hybridInput;

        // The absolute floor (design D6) is applied HERE, at the TopK call itself: only matches
        // at or above MinCosineSimilarity are ever candidates for injection, regardless of
        // source. A lexical candidate absent from this map (never embedded, or embedded but not
        // similar enough) defaults to cosine 0.0 below and is excluded by the same floor check.
        var vectorMatches = vectorIndex.TopK(queryVector.Span, VectorTopK, minCosine: _recallConfig.MinCosineSimilarity)
            .Where(m => string.Equals(m.ItemKind, MemoryEmbedOnWriteCoordinator.DocumentItemKind, StringComparison.Ordinal))
            .ToArray();
        var cosineByItemId = vectorMatches.ToDictionary(m => m.ItemId, m => m.Cosine, StringComparer.Ordinal);

        var lexicalIds = new HashSet<string>(scoredCandidates.Select(x => x.Item.Id), StringComparer.Ordinal);
        var vectorOnlyIds = vectorMatches
            .Select(m => m.ItemId)
            .Where(id => !lexicalIds.Contains(id))
            .ToArray();

        IReadOnlyList<SQLiteMemoryHydratedItem> vectorOnlyHydrated = vectorOnlyIds.Length == 0
            ? []
            : await store.GetRecallCandidatesByIdsAsync(
                vectorOnlyIds,
                deterministicPlan.AllowedMemoryClasses,
                effectiveBoundary,
                request.Audience,
                allowExpiredEvidence: false,
                ct);

        var pool = new List<(SQLiteMemoryHydratedItem Item, double SelectorScore)>(scoredCandidates.Count + vectorOnlyHydrated.Count);
        foreach (var x in scoredCandidates)
            pool.Add((x.Item, x.SelectorScore));
        foreach (var item in vectorOnlyHydrated)
            pool.Add((item, DeterministicCandidateSelector.Score(deterministicPlan, item)));

        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var fused = pool
            .Select(x =>
            {
                var cosine = cosineByItemId.GetValueOrDefault(x.Item.Id, 0.0);
                var squash = x.SelectorScore / (x.SelectorScore + SquashHalfSaturation);
                var classPrior = (RecallRank(x.Item) / RecallRankDampeningFactor) / HybridClassPriorDampeningFactor;
                var fusedScore = (_recallConfig.VectorWeight * cosine) + (_recallConfig.LexicalWeight * squash) + classPrior;
                var recencyMultiplier = RecencyMultiplier(x.Item, nowMs);
                return new RankedCandidate(x.Item, fusedScore * recencyMultiplier, cosine);
            })
            .OrderByDescending(x => x.Composite)
            .ToArray();

        // THE absolute floor (design D6): cosine alone gates admission once a query vector
        // exists — a high lexical/fused score cannot compensate for low semantic similarity.
        // Zero survivors is intended, not an error: the "Nothing relevant means nothing
        // injected" spec scenario, returned as a healthy empty result by the caller.
        var aboveFloor = fused
            .Where(x => x.Cosine is { } cosine && cosine >= _recallConfig.MinCosineSimilarity)
            .ToArray();

        return (aboveFloor, fused.Length);
    }

    /// <summary>
    /// Recency-decay multiplier applied to a candidate's fused score in hybrid mode only
    /// (memory-core-redesign Slice 4, task 4.4) — see <see cref="RecencyDecayFloor"/>/
    /// <see cref="RecencyDecayRange"/>'s remarks for the formula and its bounds. A
    /// non-positive <see cref="MemoryRecallConfig.RecencyHalfLifeDays"/> disables decay entirely
    /// (multiplier always 1.0) — the schema floors this at 1, but an operator-edited raw config
    /// bypassing the doctor check should degrade to "no decay," not divide by zero.
    /// </summary>
    private double RecencyMultiplier(SQLiteMemoryHydratedItem item, long nowMs)
    {
        var halfLifeDays = _recallConfig.RecencyHalfLifeDays;
        if (halfLifeDays <= 0)
            return 1.0;

        var ageDays = Math.Max(0.0, (nowMs - item.UpdatedAtMs) / 86_400_000.0);
        return RecencyDecayFloor + (RecencyDecayRange * Math.Pow(2.0, -ageDays / halfLifeDays));
    }

    /// <summary>
    /// Rate-limited <c>memory_recall_vector_degraded</c> log (memory-core-redesign Slice 4,
    /// task 4.1): at most one line per <paramref name="reason"/> per
    /// <see cref="VectorDegradedLogCooldown"/>. Debug when embeddings are disabled by config —
    /// the default, intentional operating mode, so this must not be Warning-level spam on every
    /// turn of a deployment that has simply never turned embeddings on (mirrors
    /// <c>MemoryCurationEvaluator</c>'s <c>curation_nominator_degraded</c> reasoning). Warning
    /// when embeddings are enabled but the turn still degraded — a genuine runtime condition an
    /// operator should notice (loud, not silent, per the spec's degradation contract).
    ///
    /// <para>
    /// Best-effort throttle: a race between two concurrent recall calls hitting the same reason
    /// at the same instant could both pass the check and both log once. Acceptable for a
    /// diagnostic throttle, not a correctness gate, so no lock is taken here.
    /// </para>
    /// </summary>
    private void LogVectorDegraded(string sessionId, string reason)
    {
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (_lastVectorDegradedLogMs.TryGetValue(reason, out var lastMs)
            && nowMs - lastMs < VectorDegradedLogCooldown.TotalMilliseconds)
            return;

        _lastVectorDegradedLogMs[reason] = nowMs;

        if (_embeddingsEnabledByConfig)
            logger.LogWarning("memory_recall_vector_degraded session={SessionId} reason={Reason}", sessionId, reason);
        else
            logger.LogDebug("memory_recall_vector_degraded session={SessionId} reason={Reason}", sessionId, reason);
    }

    private static int RecallRank(SQLiteMemoryHydratedItem document)
    {
        var score = 0;

        // Prefer deterministic durable classes and explicit/inferred semantics.
        // DurableFact 480 (May-2026 tuned set): after /100 dampening this is a
        // +4.8 composite class prior, sized against the floor of 20 so durable
        // facts clear it on ~3 lexical matches while other classes need a
        // near-perfect lexical hit — evidence/records effectively leave the
        // automatic pool unless the match is overwhelming.
        if (string.Equals(document.MemoryClass, Memory.MemoryClass.DurableFact.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 480;
        else if (string.Equals(document.MemoryClass, Memory.MemoryClass.Evidence.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 40;
        else if (string.Equals(document.MemoryClass, Memory.MemoryClass.Trace.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score -= 400;

        if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.MergeDocument.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 80;
        else if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.AppendDocument.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 60;

        if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.ImmutableRecord.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 30;

        if (string.Equals(document.Title, "verified-tool-finding", StringComparison.OrdinalIgnoreCase))
            score += 25;

        if (document.ExpiresAtMs.HasValue)
            score += 5;

        return score;
    }

    /// <summary>
    /// A candidate after fusion scoring, in either mode. <see cref="Cosine"/> is null in the
    /// degraded/lexical path (no query vector existed to compute one against) and non-null in
    /// hybrid mode (0.0 for a candidate with no recorded embedding, its true cosine otherwise).
    /// </summary>
    private readonly record struct RankedCandidate(SQLiteMemoryHydratedItem Item, double Composite, double? Cosine);
}
