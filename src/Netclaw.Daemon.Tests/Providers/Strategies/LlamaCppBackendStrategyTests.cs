// -----------------------------------------------------------------------
// <copyright file="LlamaCppBackendStrategyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        var probe = new BackendProbe("any-model", """{"object":"list","data":[]}""", PropsJson: "{}");
        Assert.True(new LlamaCppBackendStrategy().Matches(probe));
    }

    [Fact]
    public void Matches_MetaNCtxTrain_PresentEvenWithoutProps()
    {
        var probe = new BackendProbe("Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf", ModelsJsonWithMetaCtx, PropsJson: null);
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
        var probe = new BackendProbe("Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf", ModelsJsonWithMetaCtx, propsJson);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(65_536, result.ContextWindowTokens); // /props overrides
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void Parse_FallsBackToMetaNCtxTrain_WhenPropsAbsent()
    {
        var probe = new BackendProbe("Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf", ModelsJsonWithMetaCtx, PropsJson: null);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(262_144, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
    }

    [Fact]
    public void Parse_VisionDisabled_StaysTextOnly()
    {
        const string propsJson = """{"modalities":{"vision":false}}""";
        var probe = new BackendProbe("Qwen3.5", ModelsJsonWithMetaCtx, propsJson);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
    }
}
