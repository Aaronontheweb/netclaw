// -----------------------------------------------------------------------
// <copyright file="SessionDiagnosticsContextTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SessionDiagnosticsContextTests
{
    [Theory]
    [InlineData("ch/thr/subagent/skill", "ch/thr")]
    [InlineData("ch/thr", "ch/thr")]
    [InlineData("ch/thr/subagent/skill/inner", "ch/thr")]
    public void NormalizeSessionId_collapses_subagent_marker_to_parent_id(string input, string expected)
    {
        Assert.Equal(expected, SessionDiagnosticsContext.NormalizeSessionId(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeSessionId_returns_null_for_blank(string? input)
    {
        Assert.Null(SessionDiagnosticsContext.NormalizeSessionId(input));
    }
}
