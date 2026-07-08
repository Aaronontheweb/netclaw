// -----------------------------------------------------------------------
// <copyright file="MemoryConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the cross-session memory subsystem.
/// SQLite-backed durable memory settings.
/// </summary>
public sealed class MemoryConfig
{
    /// <summary>
    /// When false, the entire cross-session memory subsystem is disabled.
    /// Tools and automatic recall are not wired up regardless of audience profile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Automatic recall timeout budget in milliseconds.
    /// </summary>
    public int RecallTimeoutMs { get; set; } = 300;

    /// <summary>
    /// Maximum number of items injected into the automatic recall bundle.
    /// </summary>
    public int AutoRecallMaxItems { get; set; } = 3;

    /// <summary>
    /// Embedding-based semantic memory settings (memory-core-redesign Slice 2: embedding
    /// foundation). See <see cref="MemoryEmbeddingsConfig.Enabled"/> for why this defaults off.
    /// </summary>
    public MemoryEmbeddingsConfig Embeddings { get; set; } = new();

    /// <summary>
    /// Write-side curation settings (memory-core-redesign Slice 3: nominate→decide +
    /// lossless merge). See <see cref="MemoryCurationConfig"/>.
    /// </summary>
    public MemoryCurationConfig Curation { get; set; } = new();

    /// <summary>
    /// Read-side hybrid recall settings (memory-core-redesign Slice 4: weighted lexical/vector
    /// fusion + absolute cosine floor). See <see cref="MemoryRecallConfig"/>.
    /// </summary>
    public MemoryRecallConfig Recall { get; set; } = new();
}

/// <summary>
/// Configuration for the in-process ONNX embedding runtime (memory-core-redesign D1/D2).
/// </summary>
public sealed class MemoryEmbeddingsConfig
{
    /// <summary>
    /// When true, the daemon provisions/loads the embedding model at startup
    /// (<c>EmbeddingWarmupHostedService</c>) and computes embeddings on memory writes.
    /// Defaults to <b>false</b> for Slice 2 ("embedding foundation"): this slice only writes
    /// vectors — nothing in the write or read path consumes them yet (nominate/decide dedup is
    /// Slice 3, hybrid recall is Slice 4). Flipping this default to <c>true</c> is a deliberate
    /// decision left to whichever of those slices ships first, not an oversight here.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Allowlisted embedding model id (see <c>EmbeddingModelProvisioner.Allowlist</c> in
    /// <c>Netclaw.Embeddings</c>). An id absent from the allowlist is a configuration error,
    /// surfaced by the doctor check and warmup service — never a silently-accepted arbitrary
    /// model source (supply-chain boundary, design D2).
    /// </summary>
    public string ModelId { get; set; } = "snowflake-arctic-embed-m";

    /// <summary>
    /// When true, the daemon downloads the model artifact at startup if not already
    /// provisioned. When false, a missing or invalid model is a loud degraded-mode condition
    /// (doctor error, daemon status <c>embeddings: degraded</c>) rather than a silent network
    /// fetch — operators can pre-provision the model file (or run
    /// <c>netclaw memory backfill-embeddings</c> after manually placing it) to stay fully
    /// offline.
    /// </summary>
    public bool AutoDownload { get; set; } = true;
}

/// <summary>
/// Configuration for write-side curation: the embedding kNN nominator and the curation LLM
/// call (memory-core-redesign Slice 3, design D4/D5).
/// </summary>
public sealed class MemoryCurationConfig
{
    /// <summary>
    /// Embedding cosine similarity threshold above which an existing memory is nominated as a
    /// dedup candidate, forcing the curator LLM to adjudicate the relationship (design D4: "no
    /// cosine threshold separates duplicates from siblings," so similarity only nominates —
    /// it never auto-merges or auto-skips). Consumed by
    /// <see cref="Netclaw.Actors.Memory.MemoryCurationEvaluator"/>'s embedding kNN nominator
    /// (memory-core-redesign Slice 3 Stage B, task 3.1) via
    /// <c>Netclaw.Actors.Memory.MemoryVectorIndex.TopK</c>.
    /// </summary>
    public double NominatorSimilarityThreshold { get; set; } = 0.86;

    /// <summary>
    /// Maximum number of nearest-neighbor nominees the kNN nominator shortlists per proposal.
    /// See <see cref="NominatorSimilarityThreshold"/>'s remarks — same Slice 3 Stage B consumer.
    /// </summary>
    public int NominatorK { get; set; } = 5;

    /// <summary>
    /// Maximum output tokens for the curation LLM call
    /// (<see cref="Netclaw.Actors.Memory.MemoryCurationEvaluator"/>'s
    /// <c>TryLlmEvaluationAsync</c>). Sized generously by default: the token cap is the third
    /// line of defense against a truncated reply (after reasoning suppression and the call
    /// timeout below), so it must never be the binding constraint — the July 2026 audit found
    /// a 512-token cap produced zero successful curation decisions ever, because a
    /// reasoning-capable model was truncated mid-think before emitting its answer. Raising
    /// this further is nearly free (unemitted tokens cost nothing); lowering it below what a
    /// verbose merged body needs risks reproducing that failure with the new merged-body
    /// protocol (task 3.2).
    /// </summary>
    public int LlmMaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// Wall-clock timeout, in seconds, for the curation LLM call. Bounds latency when a model
    /// ignores reasoning suppression and thinks at length regardless of the token cap above.
    /// </summary>
    public int LlmTimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Configuration for read-side hybrid recall: weighted lexical/vector fusion and the absolute
/// cosine floor (memory-core-redesign Slice 4, design D6). Consumed by
/// <see cref="Netclaw.Actors.Sessions.SQLiteMemoryRecallCoordinator"/>. Every property is
/// defaulted, so no operator configuration is required once
/// <see cref="MemoryEmbeddingsConfig.Enabled"/> is also on — a turn with no query vector
/// available (embedder unavailable, over its sub-budget, or embeddings disabled) degrades to
/// the pre-Slice-4 lexical-only composite floor unchanged, regardless of these values.
/// </summary>
public sealed class MemoryRecallConfig
{
    /// <summary>
    /// Weight applied to a candidate's cosine similarity in the hybrid fusion score
    /// (<c>fused = VectorWeight*cosine + LexicalWeight*squash(selectorScore) + classPrior</c>,
    /// then recency-decayed). Only used in hybrid mode (a query vector was produced); ignored by
    /// the lexical-only degraded path.
    /// </summary>
    public double VectorWeight { get; set; } = 0.7;

    /// <summary>
    /// Weight applied to a candidate's squashed lexical selector score in the hybrid fusion
    /// score. See <see cref="VectorWeight"/> for the full formula.
    /// </summary>
    public double LexicalWeight { get; set; } = 0.3;

    /// <summary>
    /// Absolute relevance floor (design D6): when a query vector is available, any candidate —
    /// vector- or lexical-sourced — whose cosine similarity to the query falls below this value
    /// is dropped before ranking, regardless of fused score. Nothing surviving means nothing is
    /// injected and the <c>[memory-recall]</c> block is omitted entirely — a healthy empty
    /// result, not a degraded one. Calibrated (not a placeholder) against the real-traffic gold
    /// set (<c>gold-prod-2026-07</c>, 2026-07-05): maximizes F0.5 for the shipped fp32
    /// <c>snowflake-arctic-embed-m</c> embedder; see design D6 for the full sweep.
    /// </summary>
    public double MinCosineSimilarity { get; set; } = 0.68;

    /// <summary>
    /// Half-life, in days, for the recency-decay multiplier applied to a candidate's fused score
    /// in hybrid mode (<c>0.85 + 0.15 * 2^(-ageDays/RecencyHalfLifeDays)</c>). Floor-bounded at
    /// 0.85 by construction (the decay term is always in (0, 1] for non-negative age), so an
    /// old-but-otherwise-strong match is downweighted only enough to break ties toward fresher
    /// knowledge, never zeroed by age alone. Age is measured from the item's
    /// <c>updated_at</c> timestamp against <see cref="TimeProvider.GetUtcNow"/>.
    /// </summary>
    public double RecencyHalfLifeDays { get; set; } = 30;
}
