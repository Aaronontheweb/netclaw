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
/// <b>Hybrid recall (memory-core-redesign Slice 4 Stage B, design D6):</b> when
/// <paramref name="embedderHolder"/>'s current embedder is available and
/// <paramref name="vectorIndexHolder"/> is wired, each turn embeds the query once (a 150 ms
/// sub-budget nested inside the overall <c>Memory.RecallTimeoutMs</c> via a linked CTS) and
/// unions FTS5 lexical candidates with the vector index's top-k cosine matches. Vector-sourced
/// hits are hydrated through <see cref="SQLiteMemoryStore.GetRecallCandidatesByIdsAsync"/>,
/// which applies the IDENTICAL policy predicates <see cref="SQLiteMemoryStore.SearchByPlanAsync"/>
/// applies to lexical hits — a vector hit can never bypass a gate a lexical one would have to
/// clear. Scoring fuses a weighted cosine + squashed lexical-selector-score composite, then
/// applies an ABSOLUTE floor: any candidate (regardless of source) whose cosine falls below
/// <see cref="MemoryRecallConfig.MinCosineSimilarity"/> is dropped before ranking. Zero
/// survivors means zero injection — the caller (<see cref="SessionMessageAssembler.BuildVolatileContextBlock"/>)
/// already omits the <c>[memory-recall]</c> block entirely for an empty, non-degraded result.
/// </para>
///
/// <para>
/// <b>Degraded path (embedder unavailable, over its sub-budget, or no holder wired):</b> recall
/// falls back to the pre-Slice-4 lexical-only composite floor
/// (<see cref="DefaultMinimumRecallCompositeScore"/>) UNCHANGED — this is the exact path
/// <see cref="MemoryRecallScenarioTests"/> exercises and pins. A rate-limited
/// <c>memory_recall_vector_degraded</c> warning is logged on every fallback reason (loud, not
/// silent, per the spec's "Embedder degradation is loud, not silent" scenario) but throttled to
/// at most once per <see cref="VectorDegradedLogCooldown"/> per reason so a long-lived degraded
/// condition does not flood the log on every turn.
/// </para>
/// </summary>
public sealed class SQLiteMemoryRecallCoordinator(
    SQLiteMemoryStore store,
    ILogger<SQLiteMemoryRecallCoordinator> logger,
    SessionTuning? sessionTuning = null,
    MemoryEmbedderHolder? embedderHolder = null,
    MemoryVectorIndexHolder? vectorIndexHolder = null,
    MemoryConfig? memoryConfig = null) : IMemoryRecallCoordinator
{
    private readonly SessionTuning _sessionTuning = sessionTuning ?? new SessionTuning();
    private readonly MemoryRecallConfig _recallConfig = (memoryConfig ?? new MemoryConfig()).Recall;
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
    /// This floor governs the DEGRADED (lexical-only) path exclusively (memory-core-redesign
    /// Slice 4 Stage B). When a query vector is available the absolute cosine floor
    /// (<see cref="MemoryRecallConfig.MinCosineSimilarity"/>) governs admission instead — the
    /// two floors are never both applied to the same candidate set.
    /// </para>
    /// </summary>
    private const double DefaultMinimumRecallCompositeScore = 14.0;

    /// <summary>
    /// Sub-budget for the per-turn query embedding call, nested inside the overall
    /// <c>Memory.RecallTimeoutMs</c> via a linked CTS (memory-core-redesign Slice 4, design D6).
    /// Dynamic-length embedding (Slice 4 Stage A) measured short-query p50 ≈ 19-20 ms / p95 ≈
    /// 20.9-22.6 ms on the reference box — this budget leaves roughly 6-7x headroom over that
    /// measurement (not the literal number) so a moderately loaded host does not flap into the
    /// degraded path on every turn.
    /// </summary>
    private const int VectorSubBudgetMs = 150;

    /// <summary>
    /// Number of nearest-neighbor vector candidates fetched per recall turn (design D6). Sized
    /// well above <c>Memory.AutoRecallMaxItems</c> since the union with lexical candidates and
    /// the absolute cosine floor both shrink the pool before the outer MaxItems/char-budget
    /// bounds apply.
    /// </summary>
    private const int VectorTopK = 10;

    /// <summary>
    /// Minimum interval between two <c>memory_recall_vector_degraded</c> log lines for the SAME
    /// reason. Loud-but-not-spammy (spec: "Embedder degradation is loud, not silent") — a
    /// long-lived degraded condition (embeddings disabled, model unprovisioned) would otherwise
    /// log a Warning on every single turn.
    /// </summary>
    private static readonly TimeSpan VectorDegradedLogCooldown = TimeSpan.FromMinutes(5);

    // Existing lexical/degraded-mode dampening (unchanged — the composite floor 14.0 above was
    // calibrated against this exact scale).
    private const double RecallRankDampeningFactor = 100.0;

    // Hybrid mode dampens the class prior further than the lexical path: cosine (0..1) and
    // squash(selectorScore) (0..~1) are both already bounded fusion terms, so a /100 class-prior
    // term would swamp them the way it never could stand next to a raw, unbounded SelectorScore.
    private const double HybridRecallRankDampeningFactor = 1000.0;

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

                // ── Vector query embedding (memory-core-redesign Slice 4 Stage B, task 4.1) ──
                // Attempted once per turn, sub-budgeted inside the overall RecallAsync ct. ANY
                // failure here (unavailable, missing index, timeout, embed error) degrades to
                // the lexical-only path below — never fails the turn.
                var embedder = embedderHolder?.Current;
                MemoryVectorIndex? vectorIndex = null;
                ReadOnlyMemory<float>? queryVector = null;

                if (embedder is null)
                {
                    LogVectorDegraded(request.SessionId.Value, "no_embedder_configured");
                }
                else if (!embedder.IsAvailable)
                {
                    LogVectorDegraded(request.SessionId.Value, "embedder_unavailable");
                }
                else if (vectorIndexHolder is null)
                {
                    LogVectorDegraded(request.SessionId.Value, "no_vector_index_configured");
                }
                else
                {
                    try
                    {
                        vectorIndex = await vectorIndexHolder.GetCurrentAsync(embedder, ct);
                        if (vectorIndex is null)
                        {
                            LogVectorDegraded(request.SessionId.Value, "vector_index_unavailable");
                        }
                        else
                        {
                            using var vectorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            vectorCts.CancelAfter(VectorSubBudgetMs);
                            queryVector = await embedder.EmbedAsync(request.Query, vectorCts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        vectorIndex = null;
                        LogVectorDegraded(
                            request.SessionId.Value,
                            ex is OperationCanceledException ? "sub_budget_exceeded" : $"embed_failed:{ex.GetType().Name}");
                    }
                }

                var deterministicMaxItems = request.MaxItems <= 0 ? 3 : request.MaxItems;
                var minimumCompositeScore = _sessionTuning.MinimumRecallCompositeScore ?? DefaultMinimumRecallCompositeScore;
                var nowMs = store.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();

                string mode;
                int totalConsidered;
                int vectorCandidateCount = 0;
                double? cosineFloorApplied = null;
                RankedCandidate[] aboveFloor;

                if (queryVector is { } qv && vectorIndex is not null)
                {
                    mode = "hybrid";

                    // Vector top-k: raw cosine, no floor applied yet — the floor is applied
                    // uniformly below across BOTH sources (design D6: "all candidates passing
                    // the identical policy gates regardless of source").
                    var vectorMatches = vectorIndex.TopK(qv.Span, VectorTopK, minCosine: 0.0)
                        .Where(m => string.Equals(m.ItemKind, MemoryEmbedOnWriteCoordinator.DocumentItemKind, StringComparison.Ordinal))
                        .ToArray();
                    vectorCandidateCount = vectorMatches.Length;

                    var lexicalIdSet = new HashSet<string>(scoredCandidates.Select(x => x.Item.Id), StringComparer.Ordinal);
                    var vectorOnlyIds = vectorMatches
                        .Select(m => m.ItemId)
                        .Where(id => !lexicalIdSet.Contains(id))
                        .ToArray();

                    // Hydrate vector-only hits through the SAME policy predicates the lexical
                    // path's SearchByPlanAsync applies (security: memory-core-redesign Slice 4
                    // task 4.2, spec scenario "Vector-sourced candidates obey policy gates").
                    IReadOnlyList<SQLiteMemoryHydratedItem> vectorOnlyHydrated = vectorOnlyIds.Length == 0
                        ? []
                        : await store.GetRecallCandidatesByIdsAsync(
                            vectorOnlyIds,
                            effectiveBoundary,
                            request.Audience,
                            deterministicPlan.AllowedMemoryClasses,
                            allowExpiredEvidence: false,
                            ct);

                    // Single-pass cosine lookup covering every candidate that MIGHT have an
                    // embedding — both the vector top-k ids and every lexical hit (task 4.3: "a
                    // lexical hit that has an embedding should get its true cosine"), not just
                    // whichever ids happened to land in the global top-10.
                    var idsNeedingCosine = new HashSet<string>(lexicalIdSet, StringComparer.Ordinal);
                    foreach (var m in vectorMatches)
                        idsNeedingCosine.Add(m.ItemId);
                    var cosineById = vectorIndex.CosineForIds(idsNeedingCosine, qv.Span);

                    var pool = new List<(SQLiteMemoryHydratedItem Item, double SelectorScore)>(scoredCandidates.Count + vectorOnlyHydrated.Count);
                    foreach (var x in scoredCandidates)
                        pool.Add((x.Item, x.SelectorScore));
                    foreach (var item in vectorOnlyHydrated)
                        pool.Add((item, 0.0));

                    var hybridRanked = pool
                        .Select(x =>
                        {
                            // 0 for a candidate with no recorded embedding at all — vector
                            // component is genuinely absent evidence, not a bad match.
                            var cosine = cosineById.GetValueOrDefault(x.Item.Id, 0.0);
                            // 0 for a vector-only hit (SelectorScore 0 → squash(0) == 0).
                            var squash = x.SelectorScore / (x.SelectorScore + 10.0);
                            var classPrior = RecallRank(x.Item) / HybridRecallRankDampeningFactor;
                            var composite = (_recallConfig.VectorWeight * cosine + _recallConfig.LexicalWeight * squash + classPrior)
                                            * RecencyMultiplier(x.Item, nowMs);
                            return new RankedCandidate(x.Item, composite, cosine);
                        })
                        .OrderByDescending(x => x.Composite)
                        .ToArray();

                    totalConsidered = hybridRanked.Length;
                    cosineFloorApplied = _recallConfig.MinCosineSimilarity;

                    // THE absolute floor (design D6): cosine alone gates admission when a query
                    // vector exists. Zero survivors is intended, not an error — it is the "Nothing
                    // relevant means nothing injected" spec scenario.
                    aboveFloor = hybridRanked
                        .Where(x => x.Cosine >= _recallConfig.MinCosineSimilarity)
                        .ToArray();
                }
                else
                {
                    mode = "lexical";

                    var rankedCandidates = scoredCandidates
                        .Select(x => new RankedCandidate(
                            x.Item,
                            (x.SelectorScore + (RecallRank(x.Item) / RecallRankDampeningFactor)) * RecencyMultiplier(x.Item, nowMs),
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
                var cosineByItemId = new Dictionary<string, double>(StringComparer.Ordinal);
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
                    if (x.Cosine is { } cos)
                        cosineByItemId[x.Item.Id] = cos;
                }

                var deterministicItems = budgeted.ToArray();

                logger.LogInformation(
                    "memory_retrieval_final session={SessionId} mode={Mode} injectedCount={InjectedCount} filteredByFloor={FilteredByFloor} appliedFloor={AppliedFloor:F1} vectorCandidates={VectorCandidates} cosineFloor={CosineFloor} injectedChars={InjectedChars} droppedByBudget={DroppedByBudget} items={Items}",
                    request.SessionId,
                    mode,
                    deterministicItems.Length,
                    totalConsidered - aboveFloor.Length,
                    minimumCompositeScore,
                    vectorCandidateCount,
                    cosineFloorApplied.HasValue ? cosineFloorApplied.Value.ToString("F3") : "n/a",
                    injectedChars,
                    droppedByBudget,
                    string.Join("|", deterministicItems.Select(i => cosineByItemId.TryGetValue(i.Id.Value, out var cos)
                        ? $"{i.Id.Value}=score{i.Score:F3},cos{cos:F3}"
                        : $"{i.Id.Value}=score{i.Score:F3}")));

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
    /// Recency-decay multiplier (memory-core-redesign Slice 4 task 4.4): applies in BOTH the
    /// hybrid and degraded/lexical scoring paths. Floor-bounded at 0.7 so an old-but-otherwise-
    /// strong match is downweighted, never zeroed by age alone. Age is measured against the
    /// item's <see cref="SQLiteMemoryHydratedItem.UpdatedAtMs"/> — the same freshness/updated
    /// timestamp <see cref="SQLiteMemoryStore.SearchByPlanAsync"/> already surfaces for both
    /// documents (updated_at) and records (created_at aliased to updated_at) — via
    /// <see cref="SQLiteMemoryStore.TimeProvider"/>, the same clock the store persists
    /// timestamps with.
    /// </summary>
    private double RecencyMultiplier(SQLiteMemoryHydratedItem item, long nowMs)
    {
        var halfLifeDays = _recallConfig.RecencyHalfLifeDays;
        if (halfLifeDays <= 0)
            return 1.0;

        var ageDays = Math.Max(0.0, (nowMs - item.UpdatedAtMs) / 86_400_000.0);
        var decay = Math.Pow(2.0, -ageDays / halfLifeDays);
        return Math.Max(0.7, decay);
    }

    /// <summary>
    /// Rate-limited <c>memory_recall_vector_degraded</c> warning (memory-core-redesign Slice 4
    /// task 4.1): at most one log line per <paramref name="reason"/> per
    /// <see cref="VectorDegradedLogCooldown"/>. Best-effort — a race between two concurrent
    /// recall calls hitting the same reason at the same instant could both pass the check and
    /// both log once. That is acceptable for a diagnostic throttle, not a correctness gate, so
    /// no lock is taken here.
    /// </summary>
    private void LogVectorDegraded(string sessionId, string reason)
    {
        var nowMs = store.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (_lastVectorDegradedLogMs.TryGetValue(reason, out var lastMs)
            && nowMs - lastMs < VectorDegradedLogCooldown.TotalMilliseconds)
            return;

        _lastVectorDegradedLogMs[reason] = nowMs;
        logger.LogWarning("memory_recall_vector_degraded session={SessionId} reason={Reason}", sessionId, reason);
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
    /// hybrid mode (0.0 for a candidate with no recorded embedding at all, its true cosine
    /// otherwise).
    /// </summary>
    private readonly record struct RankedCandidate(SQLiteMemoryHydratedItem Item, double Composite, double? Cosine);
}
