// -----------------------------------------------------------------------
// <copyright file="NetclawTuiChromeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Termina.Layout;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class NetclawTuiChromeTests
{
    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("Netclaw")]
    [InlineData("")]
    [InlineData(null)]
    public void SeedTextInput_PutsTheCursorAtTheEndOfTheSeed(string? seed)
    {
        var input = new TextInputNode();

        NetclawTuiChrome.SeedTextInput(input, seed);

        Assert.Equal(seed ?? string.Empty, input.Text);
        Assert.Equal((seed ?? string.Empty).Length, input.CursorPosition);
    }

    [Fact]
    public void SeedTextInput_ReseedingAnExistingNodeMovesTheCursorToTheNewEnd()
    {
        var input = new TextInputNode();
        NetclawTuiChrome.SeedTextInput(input, "short");

        NetclawTuiChrome.SeedTextInput(input, "a-much-longer-second-value");

        Assert.Equal("a-much-longer-second-value".Length, input.CursorPosition);
    }

    [Fact]
    public void SeedTextInput_OnAFocusedNode_TypedTextAppendsToTheSeed()
    {
        var input = new TextInputNode();
        input.OnFocused();

        NetclawTuiChrome.SeedTextInput(input, "abc");
        input.HandleInput(new ConsoleKeyInfo('Z', ConsoleKey.Z, shift: true, alt: false, control: false));

        Assert.Equal("abcZ", input.Text);
    }
}
