// -----------------------------------------------------------------------
// <copyright file="InitConsoleMenuPromptTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Init;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Init;

public sealed class InitConsoleMenuPromptTests
{
    [Theory]
    [InlineData("1", InitMenuAction.RedoIdentity)]
    [InlineData("2", InitMenuAction.OpenConfig)]
    [InlineData("3", InitMenuAction.StartOver)]
    [InlineData("4", InitMenuAction.Cancel)]
    public void PromptExistingInstallMenu_NumericInput_MapsToCorrectAction(
        string input, InitMenuAction expected)
    {
        var output = new StringWriter();
        using var reader = new StringReader(input + "\n");

        var action = InitConsoleMenuPrompt.PromptExistingInstallMenu(reader, output);

        Assert.Equal(expected, action);

        var rendered = output.ToString();
        Assert.Contains("Redo identity setup", rendered);
        Assert.Contains("Open configuration editor", rendered);
        Assert.Contains("Start over from scratch", rendered);
        Assert.Contains("Cancel", rendered);
    }

    [Theory]
    [InlineData("")] // EOF on first read
    [InlineData("\n")] // blank line
    [InlineData("99")] // out of range
    [InlineData("abc")] // unparseable
    public void PromptExistingInstallMenu_BadInput_DefaultsToCancel(string input)
    {
        var output = new StringWriter();
        using var reader = new StringReader(input);

        var action = InitConsoleMenuPrompt.PromptExistingInstallMenu(reader, output);

        Assert.Equal(InitMenuAction.Cancel, action);
    }

    [Theory]
    [InlineData("1", InitStartOverAction.ResetSetup)]
    [InlineData("2", InitStartOverAction.FullReset)]
    [InlineData("3", InitStartOverAction.Cancel)]
    public void PromptStartOverDialog_MapsToAction(string input, InitStartOverAction expected)
    {
        var output = new StringWriter();
        using var reader = new StringReader(input + "\n");

        var action = InitConsoleMenuPrompt.PromptStartOverDialog(reader, output);

        Assert.Equal(expected, action);

        var rendered = output.ToString();
        Assert.Contains("Reset setup only", rendered);
        Assert.Contains("Full reset", rendered);
        // Both destructive options' descriptions render.
        Assert.Contains("workspaces, sessions, memory, skills", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmDestructiveAction_NonDestructive_AutoConfirms()
    {
        var output = new StringWriter();
        using var reader = new StringReader("");
        var ok = InitConsoleMenuPrompt.ConfirmDestructiveAction(
            InitStartOverAction.Cancel, reader, output);
        Assert.True(ok);
    }

    [Fact]
    public void ConfirmDestructiveAction_BothYes_Authorizes()
    {
        var output = new StringWriter();
        using var reader = new StringReader("yes\nyes\n");
        var ok = InitConsoleMenuPrompt.ConfirmDestructiveAction(
            InitStartOverAction.FullReset, reader, output);
        Assert.True(ok);
    }

    [Fact]
    public void ConfirmDestructiveAction_FirstNo_Refuses()
    {
        var output = new StringWriter();
        using var reader = new StringReader("no\nyes\n");
        var ok = InitConsoleMenuPrompt.ConfirmDestructiveAction(
            InitStartOverAction.FullReset, reader, output);
        Assert.False(ok);
        Assert.Contains("Cancelled", output.ToString());
    }

    [Fact]
    public void ConfirmDestructiveAction_SecondNo_Refuses()
    {
        var output = new StringWriter();
        using var reader = new StringReader("yes\nno\n");
        var ok = InitConsoleMenuPrompt.ConfirmDestructiveAction(
            InitStartOverAction.ResetSetup, reader, output);
        Assert.False(ok);
        Assert.Contains("Cancelled", output.ToString());
    }

    [Fact]
    public void ConfirmDestructiveAction_RequiresLiteralYes_NotJustY()
    {
        var output = new StringWriter();
        using var reader = new StringReader("y\ny\n");
        var ok = InitConsoleMenuPrompt.ConfirmDestructiveAction(
            InitStartOverAction.FullReset, reader, output);
        Assert.False(ok);
    }
}
