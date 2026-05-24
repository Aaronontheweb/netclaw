// -----------------------------------------------------------------------
// <copyright file="InitStartOverDialogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Init;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Init;

public sealed class InitStartOverDialogTests
{
    [Fact]
    public void Choices_AreInSpecLockedOrder()
    {
        var ids = InitStartOverDialog.Choices.Select(c => c.Id).ToArray();
        Assert.Equal(new[] { "reset-setup", "full-reset", "cancel" }, ids);
    }

    [Fact]
    public void Choices_HaveExactlyThree()
    {
        Assert.Equal(3, InitStartOverDialog.Choices.Count);
    }

    [Fact]
    public void DestructiveActions_RequireDoubleConfirmation()
    {
        Assert.True(InitStartOverDialog.RequiresDoubleConfirmation(InitStartOverAction.ResetSetup));
        Assert.True(InitStartOverDialog.RequiresDoubleConfirmation(InitStartOverAction.FullReset));
    }

    [Fact]
    public void CancelAction_DoesNotRequireConfirmation()
    {
        Assert.False(InitStartOverDialog.RequiresDoubleConfirmation(InitStartOverAction.Cancel));
    }

    [Fact]
    public void Resolve_KnownId_ReturnsChoice()
    {
        var choice = InitStartOverDialog.Resolve("full-reset");
        Assert.Equal(InitStartOverAction.FullReset, choice.Action);
        Assert.Contains("EVERYTHING", choice.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Choices_IncludeDistinctDescriptions()
    {
        var descriptions = InitStartOverDialog.Choices.Select(c => c.Description).ToArray();
        Assert.Equal(descriptions.Length, descriptions.Distinct(StringComparer.Ordinal).Count());
    }
}
