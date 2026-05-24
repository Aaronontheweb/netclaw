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
            ["model"] = "venice-uncensored",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" })
        };

        var result = PipelinePolicyTestHarness.RunSync(policy, body);

        Assert.NotNull(result);
        Assert.False(result!["venice_parameters"]?["include_venice_system_prompt"]?.GetValue<bool>());
    }

    [Fact]
    public void PreservesExistingFields()
    {
        var policy = new VeniceAiSystemPromptOverridePolicy();
        var body = new JsonObject
        {
            ["model"] = "llama-3.3-70b",
            ["temperature"] = 0.7,
            ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" })
        };

        var result = PipelinePolicyTestHarness.RunSync(policy, body);

        Assert.NotNull(result);
        Assert.Equal("llama-3.3-70b", result!["model"]?.GetValue<string>());
        Assert.Equal(0.7, result["temperature"]?.GetValue<double>());
        Assert.True(result["stream"]?.GetValue<bool>());
        Assert.Single(result["messages"]!.AsArray());
    }

    [Fact]
    public void OverwritesCallerSuppliedIncludeVeniceSystemPromptTrue()
    {
        // The policy is an unconditional clamp when attached. Operator opt-out
        // is at the plugin layer (VeniceAiProviderPlugin doesn't attach the
        // policy when IncludeVeniceSystemPrompt=true). At this layer there is
        // no escape hatch — even if upstream code somehow set it to true, we
        // force it back to false.
        var policy = new VeniceAiSystemPromptOverridePolicy();
        var body = new JsonObject
        {
            ["model"] = "venice-uncensored",
            ["venice_parameters"] = new JsonObject
            {
                ["include_venice_system_prompt"] = true,
                ["enable_web_search"] = "auto"
            }
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
            ["model"] = "venice-uncensored",
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
    public void NoOps_WhenContentIsNull()
    {
        var policy = new VeniceAiSystemPromptOverridePolicy();
        var capture = new PipelinePolicyTestHarness.CapturePolicy();
        var message = PipelinePolicyTestHarness.CreateMessage(null);

        policy.Process(message, [policy, capture], 0);

        Assert.True(capture.WasCalled);
        Assert.Null(message.Request.Content);
    }
}
