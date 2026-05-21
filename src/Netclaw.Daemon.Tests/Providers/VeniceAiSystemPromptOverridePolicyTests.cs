// -----------------------------------------------------------------------
// <copyright file="VeniceAiSystemPromptOverridePolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Providers.VeniceAi;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class VeniceAiSystemPromptOverridePolicyTests
{
    [Fact]
    public void InjectsIncludeVeniceSystemPromptFalse_WhenAbsent()
    {
        var body = new JsonObject
        {
            ["model"] = "venice-uncensored",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" })
        };

        var result = ProcessSync(body);

        Assert.NotNull(result);
        Assert.False(result!["venice_parameters"]?["include_venice_system_prompt"]?.GetValue<bool>());
    }

    [Fact]
    public void PreservesExistingFields()
    {
        var body = new JsonObject
        {
            ["model"] = "llama-3.3-70b",
            ["temperature"] = 0.7,
            ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" })
        };

        var result = ProcessSync(body);

        Assert.NotNull(result);
        Assert.Equal("llama-3.3-70b", result!["model"]?.GetValue<string>());
        Assert.Equal(0.7, result["temperature"]?.GetValue<double>());
        Assert.True(result["stream"]?.GetValue<bool>());
        Assert.Single(result["messages"]!.AsArray());
    }

    [Fact]
    public void OverwritesCallerSuppliedIncludeVeniceSystemPromptTrue()
    {
        // Even if upstream code accidentally set include_venice_system_prompt=true
        // (e.g., via a vendor-options pathway later), the policy must clamp it to
        // false. This is the security/identity-grounding gate — no escape hatch
        // at this layer.
        var body = new JsonObject
        {
            ["model"] = "venice-uncensored",
            ["venice_parameters"] = new JsonObject
            {
                ["include_venice_system_prompt"] = true,
                ["enable_web_search"] = "auto"
            }
        };

        var result = ProcessSync(body);

        Assert.NotNull(result);
        Assert.False(result!["venice_parameters"]?["include_venice_system_prompt"]?.GetValue<bool>());
    }

    [Fact]
    public void PreservesOtherVeniceParameters()
    {
        var body = new JsonObject
        {
            ["model"] = "venice-uncensored",
            ["venice_parameters"] = new JsonObject
            {
                ["enable_web_search"] = "auto",
                ["disable_thinking"] = true
            }
        };

        var result = ProcessSync(body);

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
        var pipeline = new CapturePolicy();
        var message = CreateMessage((JsonObject?)null);

        policy.Process(message, [policy, pipeline], 0);

        Assert.True(pipeline.WasCalled);
        Assert.Null(message.Request.Content);
    }

    private static JsonObject? ProcessSync(JsonObject body)
    {
        var policy = new VeniceAiSystemPromptOverridePolicy();
        var pipeline = new CapturePolicy();
        var message = CreateMessage(body);

        policy.Process(message, [policy, pipeline], 0);

        Assert.True(pipeline.WasCalled, "Policy must call ProcessNext");

        if (message.Request.Content is null)
            return null;

        using var stream = new MemoryStream();
        message.Request.Content.WriteTo(stream, default);
        return JsonSerializer.Deserialize<JsonObject>(stream.ToArray());
    }

    private static PipelineMessage CreateMessage(JsonObject? body)
    {
        var pipeline = ClientPipeline.Create();
        var message = pipeline.CreateMessage();

        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(bytes));
        }

        return message;
    }

    private sealed class CapturePolicy : PipelinePolicy
    {
        public bool WasCalled { get; private set; }

        public override void Process(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasCalled = true;
        }

        public override ValueTask ProcessAsync(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasCalled = true;
            return default;
        }
    }
}
