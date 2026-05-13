// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleChatClientUsageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiCompatibleChatClientUsageTests
{
    [Fact]
    public void ParseUsage_WallClockPromptMs_FillsWhenServerSilent()
    {
        // vLLM-shape: no `timings` object, no server-side prompt latency.
        const string json = """
        {
          "usage": {
            "prompt_tokens": 100,
            "completion_tokens": 25,
            "total_tokens": 125,
            "prompt_tokens_details": { "cached_tokens": 50 }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var usage = OpenAiCompatibleChatClient.ParseUsage(doc.RootElement, wallClockPromptMs: 120.0);

        Assert.NotNull(usage);
        Assert.Equal(50, usage.CachedInputTokenCount);
        Assert.NotNull(usage.AdditionalCounts);
        Assert.Equal(120_000L, usage.AdditionalCounts[TimingsKeys.PromptUs]);
    }

    [Fact]
    public void ParseUsage_WallClockPromptMs_DoesNotOverrideServerValue()
    {
        // llama.cpp-shape: server supplied prompt_ms via `timings`.
        // Wall-clock must NOT clobber it.
        const string json = """
        {
          "usage": { "prompt_tokens": 100, "completion_tokens": 50, "total_tokens": 150 },
          "timings": { "prompt_ms": 220.0 }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var usage = OpenAiCompatibleChatClient.ParseUsage(doc.RootElement, wallClockPromptMs: 999.0);

        Assert.NotNull(usage);
        Assert.Equal(220_000L, usage.AdditionalCounts![TimingsKeys.PromptUs]);
    }
}
