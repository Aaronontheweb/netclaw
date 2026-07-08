// -----------------------------------------------------------------------
// <copyright file="EmbeddingWarmupHostedService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Embeddings;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Provisions/loads the embedding model at daemon startup, warms it up with one inference call,
/// then runs a gap-repair sweep over documents missing a current-model embedding
/// (memory-core-redesign Slice 2, task 2.7). Populates <see cref="MemoryEmbedderHolder"/>, which
/// every embed-on-write and (in later slices) recall consumer resolves at time of use. Also
/// provisions/warms the post-floor relevance-gate's cross-encoder model
/// (memory-relevance-gate D4, task 1.4), populating <see cref="RelevanceScorerHolder"/> — a
/// second, independent provision-or-degrade step gated by the same
/// <c>Memory.Embeddings.Enabled</c> switch, with no gap-repair analogue (there is no per-item
/// derived state for a scoring-only model to repair).
///
/// <para>
/// <b>Never fails startup:</b> ANY failure here (missing model with <c>AutoDownload=false</c>,
/// download/hash failure, ONNX load failure) leaves the holder pointed at an
/// <see cref="UnavailableMemoryEmbedder"/> (or, for the relevance gate,
/// <see cref="UnavailableRelevanceScorer"/>) carrying the failure reason, logs
/// <c>memory_embedding_unavailable</c> (or <c>memory_relevance_gate_unavailable</c>) at error
/// level, and returns normally — degraded is a running state, not a startup fault (design D2,
/// spec "Loud degradation without silent fallback"). This runs on a background thread pool task
/// rather than blocking <see cref="StartAsync"/> so a slow/hanging download can never delay the
/// rest of the host's startup sequence either.
/// </para>
/// </summary>
internal sealed class EmbeddingWarmupHostedService(
    EmbeddingModelProvisioner provisioner,
    SQLiteMemoryStore store,
    MemoryEmbedderHolder holder,
    RelevanceScorerHolder relevanceScorerHolder,
    IReadOnlyDictionary<string, EmbeddingModelManifestEntry> allowlist,
    IReadOnlyDictionary<string, RelevanceModelManifestEntry> relevanceAllowlist,
    MemoryConfig memoryConfig,
    NetclawPaths paths,
    ILogger<EmbeddingWarmupHostedService> logger) : IHostedService
{
    /// <summary>
    /// Gap-repair batch size. Kept small and yielding between batches (task 2.7) so a large
    /// backlog on a fresh <c>Enabled=true</c> flip does not monopolize the CPU the daemon needs
    /// for everything else at startup.
    /// </summary>
    internal const int GapRepairBatchSize = 16;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => WarmUpAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Internal entry point so tests can await warmup to completion deterministically.</summary>
    internal async Task WarmUpAsync(CancellationToken ct)
    {
        if (!memoryConfig.Embeddings.Enabled)
        {
            logger.LogInformation(
                "memory_embedding_disabled reason={Reason}",
                "Memory.Embeddings.Enabled is false");
            return;
        }

        var modelId = memoryConfig.Embeddings.ModelId;

        // Prefix/floor are looked up unconditionally (success or failure below) since they
        // describe the model id, not whether it actually loaded (memory-query-prefix design
        // D2/D3) -- mirrors WarmUpRelevanceGateAsync's calibratedThreshold lookup exactly.
        allowlist.TryGetValue(modelId, out var manifestEntry);
        var queryPrefix = manifestEntry?.QueryPrefix ?? string.Empty;
        var calibratedMinCosineSimilarity = manifestEntry?.CalibratedMinCosineSimilarity;

        IMemoryEmbedder embedder;
        try
        {
            embedder = await LoadEmbedderAsync(modelId, queryPrefix, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "memory_embedding_unavailable model={ModelId} reason={Reason}", modelId, ex.Message);
            holder.Set(new UnavailableMemoryEmbedder(modelId, ex.Message), queryPrefix, calibratedMinCosineSimilarity);
            return;
        }

        holder.Set(embedder, queryPrefix, calibratedMinCosineSimilarity);
        logger.LogInformation(
            "memory_embedding_ready model={ModelId} dims={Dimensions} hasQueryPrefix={HasQueryPrefix} calibratedMinCosineSimilarity={CalibratedMinCosineSimilarity}",
            embedder.ModelId,
            embedder.Dimensions,
            queryPrefix.Length > 0,
            calibratedMinCosineSimilarity);

        try
        {
            await GapRepairAsync(embedder, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The embedder itself is already loaded and the holder is already populated — a
            // gap-repair failure (e.g. a transient store error) must not undo that or leave an
            // unobserved exception on this fire-and-forget warmup task. The doctor check and
            // the next daemon restart's sweep both retry whatever remains unembedded.
            logger.LogWarning(ex, "memory_embedding_gap_repair_failed model={ModelId}", embedder.ModelId);
        }

        // Relevance gate (memory-relevance-gate, design D4, task 1.4): a second, independent
        // provision-or-degrade step gated by the same Memory.Embeddings.Enabled switch (D6's
        // "one mental switch" — there is no separate RelevanceGate.AutoDownload/ModelId knob).
        // Runs regardless of whether the embedder itself just degraded above: the two models are
        // separately lifecycled artifacts, so an embedder failure should not also prevent an
        // attempt to provision the relevance model. No gap-repair analogue exists here — there is
        // no per-item derived state to repair for a scoring-only model.
        await WarmUpRelevanceGateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Provisions and warms the relevance (cross-encoder) model, mirroring
    /// <see cref="LoadEmbedderAsync"/>'s provision-or-degrade shape exactly. The manifest's
    /// <c>CalibratedThreshold</c> is looked up unconditionally (success or failure) since it
    /// describes the model id, not whether the model actually loaded — <see cref="RelevanceScorerHolder"/>
    /// always pairs a scorer (available or not) with the correct threshold for its model id.
    /// </summary>
    private async Task WarmUpRelevanceGateAsync(CancellationToken ct)
    {
        var modelId = EmbeddingModelProvisioner.DefaultRelevanceModelId;
        var calibratedThreshold = relevanceAllowlist.TryGetValue(modelId, out var entry)
            ? entry.CalibratedThreshold
            : 0.0;

        try
        {
            var scorer = await LoadRelevanceScorerAsync(modelId, ct).ConfigureAwait(false);
            relevanceScorerHolder.Set(scorer, calibratedThreshold);
            logger.LogInformation("memory_relevance_gate_ready model={ModelId}", scorer.ModelId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "memory_relevance_gate_unavailable model={ModelId} reason={Reason}", modelId, ex.Message);
            relevanceScorerHolder.Set(new UnavailableRelevanceScorer(modelId, ex.Message), calibratedThreshold);
        }
    }

    private async Task<IRelevanceScorer> LoadRelevanceScorerAsync(string modelId, CancellationToken ct)
    {
        // Keyed under the same ModelsDirectory root as embedding models (NetclawPaths.
        // EmbeddingModelDirectory is already generalized by model id) — a distinct id string is
        // all that's needed to avoid collisions, so no dedicated relevance-model path helper
        // exists.
        var modelDirectory = paths.EmbeddingModelDirectory(modelId);

        ProvisionedRelevanceModel provisioned;
        if (memoryConfig.Embeddings.AutoDownload)
        {
            provisioned = await provisioner.ProvisionRelevanceModelAsync(modelId, relevanceAllowlist, modelDirectory, ct)
                .ConfigureAwait(false);
        }
        else
        {
            provisioned = await provisioner.TryLoadVerifiedRelevanceModelAsync(modelId, relevanceAllowlist, modelDirectory, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Relevance model '{modelId}' is not provisioned (or failed hash verification) at " +
                    $"{modelDirectory}, and Memory.Embeddings.AutoDownload is false. Provision it manually " +
                    "or enable AutoDownload, then restart the daemon.");
        }

        var scorer = await OnnxCrossEncoderScorer.LoadAsync(provisioned.ModelPath, provisioned.VocabPath, provisioned.ModelId, ct: ct)
            .ConfigureAwait(false);

        // Warm-up inference (mirrors the embedder's own warm-up call): pays first-call ONNX
        // session / JIT cost here rather than on the first real recall turn.
        await scorer.ScoreAsync("netclaw relevance gate warmup query", ["netclaw relevance gate warmup candidate"], ct)
            .ConfigureAwait(false);

        return scorer;
    }

    private async Task<IMemoryEmbedder> LoadEmbedderAsync(string modelId, string queryPrefix, CancellationToken ct)
    {
        var modelDirectory = paths.EmbeddingModelDirectory(modelId);

        ProvisionedEmbeddingModel provisioned;
        if (memoryConfig.Embeddings.AutoDownload)
        {
            provisioned = await provisioner.ProvisionAsync(modelId, modelDirectory, ct).ConfigureAwait(false);
        }
        else
        {
            // AutoDownload=false gates the network path entirely — even to repair a corrupted
            // local copy. A missing/invalid model here is a loud degraded-mode condition, not a
            // fallback to fetching it anyway.
            provisioned = await provisioner.TryLoadVerifiedAsync(modelId, modelDirectory, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Embedding model '{modelId}' is not provisioned (or failed hash verification) at " +
                    $"{modelDirectory}, and Memory.Embeddings.AutoDownload is false. Provision it manually " +
                    "or enable AutoDownload, then restart the daemon or run `netclaw memory backfill-embeddings`.");
        }

        var embedder = await OnnxMemoryEmbedder.LoadAsync(
            provisioned.ModelPath,
            provisioned.VocabPath,
            provisioned.ModelId,
            provisioned.Dimensions,
            queryPrefix,
            ct: ct).ConfigureAwait(false);

        // Warm-up inference (design D1/D2): pays first-call ONNX session / JIT cost here rather
        // than on the first real memory write or recall query. Passage purpose: this is a
        // generic session/JIT warm-up, not a real query, so there is nothing gained from also
        // exercising the query-prefix path here (the first real recall turn pays that cost, well
        // inside its own sub-budget per design D2's negligible token-count claim).
        await embedder.EmbedAsync("netclaw embedding warmup", EmbeddingPurpose.Passage, ct).ConfigureAwait(false);

        return embedder;
    }

    /// <summary>
    /// Embeds every recallable document missing a current-model/current-hash embedding, in
    /// small batches, yielding between batches (task 2.7). This is what self-heals the gap
    /// described in design D3's failure/recovery note: a crash between a document commit and
    /// its embedding upsert leaves a missing-embedding row, which this sweep (and the embedding
    /// doctor check) both detect and repair.
    /// </summary>
    private async Task GapRepairAsync(IMemoryEmbedder embedder, CancellationToken ct)
    {
        var missing = await store.GetDocumentsNeedingEmbeddingAsync(embedder.ModelId, force: false, ct).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            logger.LogInformation("memory_embedding_gap_repair_complete embedded=0 model={ModelId}", embedder.ModelId);
            return;
        }

        var embedded = 0;
        var failed = 0;
        for (var offset = 0; offset < missing.Count; offset += GapRepairBatchSize)
        {
            var batch = missing.Skip(offset).Take(GapRepairBatchSize).ToArray();
            var texts = batch.Select(d => $"{d.Title}\n{d.Body}").ToArray();

            try
            {
                var vectors = await embedder.EmbedBatchAsync(texts, EmbeddingPurpose.Passage, ct).ConfigureAwait(false);
                for (var i = 0; i < batch.Length; i++)
                {
                    var hash = MemoryContentHasher.ComputeHash(batch[i].Title, batch[i].Body);
                    await store.UpsertEmbeddingAsync(
                        batch[i].DocumentId, MemoryEmbedOnWriteCoordinator.DocumentItemKind,
                        embedder.ModelId, hash, vectors[i], ct).ConfigureAwait(false);
                    embedded++;
                }
            }
            catch (Exception ex)
            {
                // One bad batch must not abort the sweep — the doctor check and the next
                // restart's sweep will retry whatever remains missing.
                failed += batch.Length;
                logger.LogWarning(ex, "memory_embedding_gap_repair_batch_failed count={Count}", batch.Length);
            }

            // Yield between batches so gap-repair on a large backlog does not monopolize the
            // CPU the daemon needs for everything else at startup.
            await Task.Yield();
        }

        logger.LogInformation(
            "memory_embedding_gap_repair_complete embedded={Embedded} failed={Failed} model={ModelId}",
            embedded, failed, embedder.ModelId);
    }
}
