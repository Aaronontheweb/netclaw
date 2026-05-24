// -----------------------------------------------------------------------
// <copyright file="InitResetServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Init;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Init;

public sealed class InitResetServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public InitResetServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ResetSetupOnly_RemovesConfigAndIdentity_PreservesWorkspaces()
    {
        // Seed the install with config, secrets, identity files, agents
        // directory, AND a workspaces tree.
        File.WriteAllText(_paths.NetclawConfigPath, """{"configVersion":1}""");
        File.WriteAllText(_paths.SecretsPath, "{}");
        File.WriteAllText(_paths.SoulPath, "soul");
        File.WriteAllText(_paths.ToolingPath, "tooling");
        Directory.CreateDirectory(_paths.AgentsDirectory);
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "research-assistant.md"), "agent");

        var workspacesDir = Path.Combine(_paths.BasePath, "workspaces");
        Directory.CreateDirectory(workspacesDir);
        File.WriteAllText(Path.Combine(workspacesDir, "important-project.txt"), "preserve me");

        var report = InitResetService.ResetSetupOnly(_paths);

        Assert.Equal(InitStartOverAction.ResetSetup, report.Action);
        Assert.False(File.Exists(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));
        Assert.False(File.Exists(_paths.SoulPath));
        Assert.False(File.Exists(_paths.ToolingPath));
        Assert.False(Directory.Exists(_paths.AgentsDirectory));

        // Workspaces SHALL survive a "reset setup only" — that's the entire
        // point of the distinction with Full reset.
        Assert.True(File.Exists(Path.Combine(workspacesDir, "important-project.txt")));
    }

    [Fact]
    public void ResetSetupOnly_NoExistingArtifacts_RemovesOnlyWhatExists()
    {
        // Construct a fresh path tree where no install artifacts have been
        // created (skip EnsureDirectoriesExist so the seeded folders don't
        // exist either).
        var freshDir = new DisposableTempDir();
        try
        {
            var freshPaths = new NetclawPaths(freshDir.Path);
            // Intentionally no EnsureDirectoriesExist — nothing should be there.

            var report = InitResetService.ResetSetupOnly(freshPaths);
            Assert.Equal(InitStartOverAction.ResetSetup, report.Action);
            Assert.Empty(report.RemovedPaths);
        }
        finally
        {
            freshDir.Dispose();
        }
    }

    [Fact]
    public void FullReset_WipesEverythingUnderRoot()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");
        var workspacesDir = Path.Combine(_paths.BasePath, "workspaces");
        Directory.CreateDirectory(workspacesDir);
        File.WriteAllText(Path.Combine(workspacesDir, "session.json"), "gone");

        var report = InitResetService.FullReset(_paths);

        Assert.Equal(InitStartOverAction.FullReset, report.Action);
        Assert.False(Directory.Exists(_paths.BasePath),
            "Full reset SHALL wipe the entire netclaw root tree, including workspaces.");
        Assert.Contains(_paths.BasePath, report.RemovedPaths);
    }

    [Fact]
    public void FullReset_MissingRoot_IsNoOp()
    {
        // Re-create with a non-existent root to simulate already-clean state.
        var freshDir = new DisposableTempDir();
        try
        {
            var missing = Path.Combine(freshDir.Path, "does-not-exist");
            var paths = new NetclawPaths(missing);
            var report = InitResetService.FullReset(paths);
            Assert.Empty(report.RemovedPaths);
        }
        finally
        {
            freshDir.Dispose();
        }
    }

    [Fact]
    public void Reset_NullPaths_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => InitResetService.ResetSetupOnly(null!));
        Assert.Throws<ArgumentNullException>(() => InitResetService.FullReset(null!));
    }

    [Fact]
    public void FullReset_RefusesFilesystemRoot()
    {
        // Simulate a misconfigured NETCLAW_HOME by passing a root path.
        var root = OperatingSystem.IsWindows() ? "C:\\" : "/";
        var paths = new NetclawPaths(root);

        var ex = Assert.Throws<InvalidOperationException>(() => InitResetService.FullReset(paths));
        Assert.Contains("protected root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullReset_RefusesUserProfileRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
            return; // can't run on CI without a home dir

        var paths = new NetclawPaths(home);
        var ex = Assert.Throws<InvalidOperationException>(() => InitResetService.FullReset(paths));
        Assert.Contains("protected root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullReset_RefusesDirectoryWithoutNetclawMarker()
    {
        var fresh = new DisposableTempDir();
        try
        {
            // No netclaw.json and no identity/ subdirectory — the safety
            // floor SHALL refuse.
            var paths = new NetclawPaths(fresh.Path);
            var ex = Assert.Throws<InvalidOperationException>(() => InitResetService.FullReset(paths));
            Assert.Contains("does not contain a Netclaw marker", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            fresh.Dispose();
        }
    }

    [Fact]
    public void FullReset_AcceptsDirectoryWithIdentityMarker()
    {
        // Marker check should also accept identity/ as a valid marker
        // (covers the case where netclaw.json was removed but identity files
        // are still on disk).
        var fresh = new DisposableTempDir();
        try
        {
            var paths = new NetclawPaths(fresh.Path);
            Directory.CreateDirectory(paths.IdentityDirectory);

            var report = InitResetService.FullReset(paths);
            Assert.False(Directory.Exists(paths.BasePath));
            Assert.Contains(paths.BasePath, report.RemovedPaths);
        }
        finally
        {
            fresh.Dispose();
        }
    }
}
