// -----------------------------------------------------------------------
// <copyright file="LlamaCppTimingsExtractorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class LlamaCppTimingsExtractorTests
{
    [Fact]
    public void Extracts_CacheN_AndTimings()
    {
        const string json = """
        {
          "usage": { "prompt_tokens": 100, "completion_tokens": 50, "total_tokens": 150 },
          "timings": {
            "cache_n": 2048,
            "prompt_ms": 450.5,
            "prompt_per_second": 200.0,
            "predicted_ms": 1200.0,
            "predicted_per_second": 41.67
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var details = new UsageDetails();

        new LlamaCppTimingsExtractor().Extract(doc.RootElement, details);

        Assert.Equal(2048, details.CachedInputTokenCount);
        Assert.NotNull(details.AdditionalCounts);
        Assert.Equal(450_500L, details.AdditionalCounts[TimingsKeys.PromptUs]);
        Assert.Equal(20_000L, details.AdditionalCounts[TimingsKeys.PromptTokPerSecX100]);
        Assert.Equal(1_200_000L, details.AdditionalCounts[TimingsKeys.PredictedUs]);
        Assert.Equal(4167L, details.AdditionalCounts[TimingsKeys.PredictedTokPerSecX100]);
    }

    [Fact]
    public void NoOp_WhenTimingsAbsent()
    {
        const string json = """{ "usage": { "prompt_tokens": 100 } }""";
        using var doc = JsonDocument.Parse(json);
        var details = new UsageDetails();

        new LlamaCppTimingsExtractor().Extract(doc.RootElement, details);

        Assert.Null(details.CachedInputTokenCount);
        Assert.Null(details.AdditionalCounts);
    }

    [Fact]
    public void NoOp_WhenTimingsNotObject()
    {
        const string json = """{ "timings": "not-an-object" }""";
        using var doc = JsonDocument.Parse(json);
        var details = new UsageDetails();

        new LlamaCppTimingsExtractor().Extract(doc.RootElement, details);

        Assert.Null(details.CachedInputTokenCount);
    }

    [Fact]
    public void PartialFields_OK()
    {
        // Only cache_n + prompt_ms, missing throughput fields.
        const string json = """
        {
          "timings": { "cache_n": 512, "prompt_ms": 100.0 }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var details = new UsageDetails();

        new LlamaCppTimingsExtractor().Extract(doc.RootElement, details);

        Assert.Equal(512, details.CachedInputTokenCount);
        Assert.Equal(100_000L, details.AdditionalCounts![TimingsKeys.PromptUs]);
        Assert.False(details.AdditionalCounts.ContainsKey(TimingsKeys.PredictedUs));
    }
}
