// -----------------------------------------------------------------------
// <copyright file="GenericOpenAiBackendStrategyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.Strategies;

public sealed class GenericOpenAiBackendStrategyTests
{
    [Fact]
    public void Matches_AnyShape()
    {
        using var doc = JsonDocument.Parse("""{}""");
        var probe = new BackendProbe("any", doc.RootElement, PropsRoot: null);
        Assert.True(new GenericOpenAiBackendStrategy().Matches(probe));
    }

    [Fact]
    public void Parse_ReturnsAllFieldsNull()
    {
        using var doc = JsonDocument.Parse("""{}""");
        var probe = new BackendProbe("any-model", doc.RootElement, PropsRoot: null);
        var result = new GenericOpenAiBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal("any-model", result.ModelId);
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
        Assert.Null(result.ContextWindowTokens);
    }
}
