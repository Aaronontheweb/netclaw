// -----------------------------------------------------------------------
// <copyright file="SectionValidationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections;

public sealed class SectionValidationTests
{
    [Fact]
    public void Valid_HasNoMessage_AndCannotBeBypassed()
    {
        var outcome = SectionValidationOutcome.Valid;
        Assert.Equal(SectionValidationKind.Valid, outcome.Kind);
        Assert.Null(outcome.Message);
        Assert.False(outcome.CanSaveAnyway);
    }

    [Fact]
    public void StructuralError_BlocksSave_NoOverride()
    {
        var outcome = SectionValidationOutcome.StructuralError("invalid URI");
        Assert.Equal(SectionValidationKind.StructuralError, outcome.Kind);
        Assert.Equal("invalid URI", outcome.Message);
        Assert.False(outcome.CanSaveAnyway,
            "Spec §13: structural errors SHALL block save without override.");
    }

    [Fact]
    public void ProbeFailure_PermitsSaveAnyway()
    {
        var outcome = SectionValidationOutcome.ProbeFailure("host unreachable");
        Assert.Equal(SectionValidationKind.ProbeFailure, outcome.Kind);
        Assert.Equal("host unreachable", outcome.Message);
        Assert.True(outcome.CanSaveAnyway,
            "Spec §13: probe failures MAY present Save anyway.");
    }
}
