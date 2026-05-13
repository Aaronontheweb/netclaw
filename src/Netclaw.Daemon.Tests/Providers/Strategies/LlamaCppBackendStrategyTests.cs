// -----------------------------------------------------------------------
// <copyright file="LlamaCppBackendStrategyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.Strategies;

public sealed class LlamaCppBackendStrategyTests
{
    private const string ModelsJsonWithMetaCtx = """
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

    [Fact]
    public void Matches_PropsPresent()
    {
        using var models = JsonDocument.Parse("""{"object":"list","data":[]}""");
        using var props = JsonDocument.Parse("{}");
        var probe = new BackendProbe("any-model", models.RootElement, props.RootElement);
        Assert.True(new LlamaCppBackendStrategy().Matches(probe));
    }

    [Fact]
    public void Matches_MetaNCtxTrain_PresentEvenWithoutProps()
    {
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        var probe = new BackendProbe("Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf", models.RootElement, PropsRoot: null);
        Assert.True(new LlamaCppBackendStrategy().Matches(probe));
    }

    [Fact]
    public void Parse_PrefersPropsNCtxOverMetaNCtxTrain()
    {
        const string propsJson = """
        {
          "default_generation_settings": { "params": { "n_ctx": 65536 } },
          "modalities": { "vision": true }
        }
        """;
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        using var props = JsonDocument.Parse(propsJson);
        var probe = new BackendProbe("Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf", models.RootElement, props.RootElement);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(65_536, result.ContextWindowTokens); // /props overrides
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void Parse_FallsBackToMetaNCtxTrain_WhenPropsAbsent()
    {
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        var probe = new BackendProbe("Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf", models.RootElement, PropsRoot: null);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(262_144, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
    }

    [Fact]
    public void Parse_VisionDisabled_StaysTextOnly()
    {
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        using var props = JsonDocument.Parse("""{"modalities":{"vision":false}}""");
        var probe = new BackendProbe("Qwen3.5", models.RootElement, props.RootElement);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
    }
}
