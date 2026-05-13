// -----------------------------------------------------------------------
// <copyright file="GenericOpenAiBackendStrategyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.Strategies;

public sealed class GenericOpenAiBackendStrategyTests
{
    [Fact]
    public void Matches_AnyShape()
    {
        var probe = new BackendProbe("any", """{}""", PropsJson: null);
        Assert.True(new GenericOpenAiBackendStrategy().Matches(probe));
    }

    [Fact]
    public void Parse_ReturnsAllFieldsNull()
    {
        var probe = new BackendProbe("any-model", """{}""", PropsJson: null);
        var result = new GenericOpenAiBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal("any-model", result.ModelId);
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
        Assert.Null(result.ContextWindowTokens);
    }
}
