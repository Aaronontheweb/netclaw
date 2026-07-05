// -----------------------------------------------------------------------
// <copyright file="MemoryConfigDefaultsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Bear-trap tests for <see cref="MemoryEmbeddingsConfig"/> defaults (memory-core-redesign
/// Slice 2, task 2.11). If you change a default, you must update these assertions — forcing a
/// deliberate decision rather than an accidental drift. <see cref="MemoryEmbeddingsConfig.Enabled"/>
/// defaults to false in particular: flipping it is a deliberate Slice 3/4 decision, not something
/// that should silently change because a refactor touched the property initializer.
/// </summary>
public sealed class MemoryConfigDefaultsTests
{
    [Fact]
    public void Embeddings_disabled_by_default()
    {
        var config = new MemoryConfig();
        Assert.False(config.Embeddings.Enabled);
    }

    [Fact]
    public void Embeddings_model_id_defaults_to_snowflake_arctic_embed_m()
    {
        // memory-core-redesign Slice 4 Stage A evaluated a RAM-lean int8 build as the default
        // candidate, but its measured quality parity against fp32 fell short of the acceptance
        // gate on doc-length content (tools/embed-latency-bench parity mode; numbers in
        // design.md D1/D2) — fp32 stays the default rather than shipping a degraded one. The
        // int8 build remains allowlisted as an explicit opt-in.
        var config = new MemoryConfig();
        Assert.Equal("snowflake-arctic-embed-m", config.Embeddings.ModelId);
    }

    [Fact]
    public void Embeddings_auto_download_defaults_to_true()
    {
        var config = new MemoryConfig();
        Assert.True(config.Embeddings.AutoDownload);
    }

    [Fact]
    public void Memory_subsystem_remains_enabled_by_default()
    {
        var config = new MemoryConfig();
        Assert.True(config.Enabled);
    }

    // ── MemoryCurationConfig (memory-core-redesign Slice 3, task 3.5) ──

    [Fact]
    public void Curation_nominator_similarity_threshold_defaults_to_0_86()
    {
        var config = new MemoryConfig();
        Assert.Equal(0.86, config.Curation.NominatorSimilarityThreshold);
    }

    [Fact]
    public void Curation_nominator_k_defaults_to_5()
    {
        var config = new MemoryConfig();
        Assert.Equal(5, config.Curation.NominatorK);
    }

    [Fact]
    public void Curation_llm_max_output_tokens_defaults_to_4096()
    {
        var config = new MemoryConfig();
        Assert.Equal(4096, config.Curation.LlmMaxOutputTokens);
    }

    [Fact]
    public void Curation_llm_timeout_seconds_defaults_to_10()
    {
        var config = new MemoryConfig();
        Assert.Equal(10, config.Curation.LlmTimeoutSeconds);
    }

    // ── MemoryRecallConfig (memory-core-redesign Slice 4 Stage B, task 4.5) ──

    [Fact]
    public void Recall_vector_weight_defaults_to_0_7()
    {
        var config = new MemoryConfig();
        Assert.Equal(0.7, config.Recall.VectorWeight);
    }

    [Fact]
    public void Recall_lexical_weight_defaults_to_0_3()
    {
        var config = new MemoryConfig();
        Assert.Equal(0.3, config.Recall.LexicalWeight);
    }

    [Fact]
    public void Recall_min_cosine_similarity_defaults_to_0_55()
    {
        // Placeholder pending Stage C calibration against gold-prod-2026-07 (design D6, task
        // 4.6) — if you change this, that calibration work is what should be driving it.
        var config = new MemoryConfig();
        Assert.Equal(0.55, config.Recall.MinCosineSimilarity);
    }

    [Fact]
    public void Recall_recency_half_life_days_defaults_to_180()
    {
        var config = new MemoryConfig();
        Assert.Equal(180, config.Recall.RecencyHalfLifeDays);
    }
}
