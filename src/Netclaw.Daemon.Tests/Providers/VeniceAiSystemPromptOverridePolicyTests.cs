// -----------------------------------------------------------------------
// <copyright file="VeniceAiSystemPromptOverridePolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Providers.VeniceAi;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class VeniceAiSystemPromptOverridePolicyTests
{
    [Fact]
    public void InjectsIncludeVeniceSystemPromptFalse_WhenAbsent()
    {
        var policy = new VeniceAiSystemPromptOverridePolicy();
        var body = new JsonObject
        {
            ["model"] = "venice/llama-3.3-70b",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" })
        };

        var result = PipelinePolicyTestHarness.RunSync(policy, body);

        Assert.NotNull(result);
        Assert.False(result!["venice_parameters"]?["include_venice_system_prompt"]?.GetValue<bool>());
    }

    [Fact]
    public void PreservesOtherVeniceParameters()
    {
        var policy = new VeniceAiSystemPromptOverridePolicy();
        var body = new JsonObject
        {
            ["model"] = "venice/llama-3.3-70b",
            ["venice_parameters"] = new JsonObject
            {
                ["enable_web_search"] = "auto",
                ["disable_thinking"] = true
            }
        };

        var result = PipelinePolicyTestHarness.RunSync(policy, body);

        Assert.NotNull(result);
        var veniceParams = result!["venice_parameters"]!.AsObject();
        Assert.False(veniceParams["include_venice_system_prompt"]?.GetValue<bool>());
        Assert.Equal("auto", veniceParams["enable_web_search"]?.GetValue<string>());
        Assert.True(veniceParams["disable_thinking"]?.GetValue<bool>());
    }

    [Fact]
    public void ClampsUpstreamSetTrue_ToFalse()
    {
        var policy = new VeniceAiSystemPromptOverridePolicy();
        var body = new JsonObject
        {
            ["model"] = "venice/llama-3.3-70b",
            ["venice_parameters"] = new JsonObject
            {
                ["include_venice_system_prompt"] = true
            }
        };

        var result = PipelinePolicyTestHarness.RunSync(policy, body);

        Assert.NotNull(result);
        Assert.False(result!["venice_parameters"]?["include_venice_system_prompt"]?.GetValue<bool>());
    }
}
