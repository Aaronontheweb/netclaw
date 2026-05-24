// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallMenuTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Init;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Init;

public sealed class InitExistingInstallMenuTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public InitExistingInstallMenuTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Choices_AreInSpecLockedOrder()
    {
        // Spec netclaw-onboarding/spec.md and simplify-netclaw-init §3 lock
        // these four options in this exact order. Order matters: operators
        // see them in a list and the first option SHOULD be the most common
        // (re-do identity is the typical re-entry path).
        var ids = InitExistingInstallMenu.Choices.Select(c => c.Id).ToArray();
        Assert.Equal(new[] { "redo-identity", "open-config", "start-over", "cancel" }, ids);
    }

    [Fact]
    public void Choices_HaveExactlyFour_NoOthers()
    {
        Assert.Equal(4, InitExistingInstallMenu.Choices.Count);
    }

    [Fact]
    public void IsExistingInstall_FalseWhenNoConfigFile()
    {
        Assert.False(InitExistingInstallMenu.IsExistingInstall(_paths));
    }

    [Fact]
    public void IsExistingInstall_TrueWhenConfigFilePresent()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{"configVersion":1}""");
        Assert.True(InitExistingInstallMenu.IsExistingInstall(_paths));
    }

    [Fact]
    public void Resolve_KnownId_ReturnsChoice()
    {
        var choice = InitExistingInstallMenu.Resolve("open-config");
        Assert.Equal(InitMenuAction.OpenConfig, choice.Action);
        Assert.Equal("Open configuration editor", choice.Label);
    }

    [Fact]
    public void Resolve_UnknownId_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => InitExistingInstallMenu.Resolve("force"));
    }

    [Fact]
    public void Actions_MapToCorrectIds()
    {
        Assert.Equal(InitMenuAction.RedoIdentity, InitExistingInstallMenu.Resolve("redo-identity").Action);
        Assert.Equal(InitMenuAction.OpenConfig, InitExistingInstallMenu.Resolve("open-config").Action);
        Assert.Equal(InitMenuAction.StartOver, InitExistingInstallMenu.Resolve("start-over").Action);
        Assert.Equal(InitMenuAction.Cancel, InitExistingInstallMenu.Resolve("cancel").Action);
    }
}
