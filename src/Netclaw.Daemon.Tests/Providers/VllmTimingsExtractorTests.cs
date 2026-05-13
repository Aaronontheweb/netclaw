// -----------------------------------------------------------------------
// <copyright file="VllmTimingsExtractorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class VllmTimingsExtractorTests
{
    [Fact]
    public void Extracts_CachedTokens_FromPromptTokensDetails()
    {
        const string json = """
        {
          "usage": {
            "prompt_tokens": 1024,
            "completion_tokens": 50,
            "total_tokens": 1074,
            "prompt_tokens_details": { "cached_tokens": 768 }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var details = new UsageDetails();

        new VllmTimingsExtractor().Extract(doc.RootElement, details);

        Assert.Equal(768, details.CachedInputTokenCount);
    }

    [Fact]
    public void NoOp_WhenPromptTokensDetailsAbsent()
    {
        const string json = """{ "usage": { "prompt_tokens": 100 } }""";
        using var doc = JsonDocument.Parse(json);
        var details = new UsageDetails();

        new VllmTimingsExtractor().Extract(doc.RootElement, details);

        Assert.Null(details.CachedInputTokenCount);
    }

    [Fact]
    public void NoOp_WhenUsageAbsent()
    {
        const string json = """{ "choices": [] }""";
        using var doc = JsonDocument.Parse(json);
        var details = new UsageDetails();

        new VllmTimingsExtractor().Extract(doc.RootElement, details);

        Assert.Null(details.CachedInputTokenCount);
    }
}
