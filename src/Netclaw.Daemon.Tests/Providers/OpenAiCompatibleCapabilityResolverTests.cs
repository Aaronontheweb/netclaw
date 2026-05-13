// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleCapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// High-level dispatch tests for <see cref="OpenAiCompatibleCapabilityResolver"/>.
/// Per-backend parsing is covered in <c>VllmBackendStrategyTests</c> and
/// <c>LlamaCppBackendStrategyTests</c>.
/// </summary>
public sealed class OpenAiCompatibleCapabilityResolverTests
{
    [Fact]
    public void ResolveFromProbe_VllmShape_DispatchesToVllmStrategy()
    {
        // vLLM /v1/models exposes max_model_len at the top level of the
        // model entry and owns "vllm" as the owned_by value. /props 404s.
        const string modelsJson = """
        {
          "object": "list",
          "data": [
            {
              "id": "Qwen/Qwen3.6-VL-30B-FP8",
              "object": "model",
              "owned_by": "vllm",
              "max_model_len": 256000
            }
          ]
        }
        """;
        var probe = new BackendProbe("Qwen/Qwen3.6-VL-30B-FP8", modelsJson, PropsJson: null);

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(probe);

        Assert.NotNull(result);
        Assert.Equal(256_000, result.ContextWindowTokens);
        // vLLM exposes no modality info; HF resolver fills these downstream.
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
    }

    [Fact]
    public void ResolveFromProbe_LlamaCppShape_DispatchesToLlamaCppStrategy()
    {
        // llama.cpp exposes context window in meta.n_ctx_train on /v1/models
        // and serves /props with modality and runtime n_ctx data.
        const string modelsJson = """
        {
          "object": "list",
          "data": [
            {
              "id": "Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf",
              "meta": { "n_ctx_train": 262144 }
            }
          ]
        }
        """;
        const string propsJson = """
        {
          "default_generation_settings": { "params": { "n_ctx": 65536 } },
          "modalities": { "vision": true }
        }
        """;
        var probe = new BackendProbe("Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf", modelsJson, propsJson);

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(probe);

        Assert.NotNull(result);
        // /props.n_ctx wins over meta.n_ctx_train when both are present.
        Assert.Equal(65_536, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ResolveFromProbe_UnknownShape_FallsThroughToGenericStrategy()
    {
        const string modelsJson = """
        { "object": "list", "data": [ { "id": "mystery-model" } ] }
        """;
        var probe = new BackendProbe("mystery-model", modelsJson, PropsJson: null);

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(probe);

        Assert.NotNull(result);
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
        Assert.Null(result.ContextWindowTokens);
    }
}
