// -----------------------------------------------------------------------
// <copyright file="DaemonManagerSingletonGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonManagerSingletonGuardTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly DaemonManager _sut;

    public DaemonManagerSingletonGuardTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _sut = new DaemonManager(_paths, TimeProvider.System);
    }

    [Fact]
    public void IsLockFileHeld_ReturnsFalse_WhenNoLock()
    {
        Assert.False(_sut.IsLockFileHeld());
    }

    [Fact]
    public void IsLockFileHeld_ReturnsTrue_WhenLockHeld()
    {
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.True(_sut.IsLockFileHeld());
    }

    [Fact]
    public void IsLockFileHeld_ReturnsFalse_AfterLockReleased()
    {
        // Acquire and release
        using (var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.True(_sut.IsLockFileHeld());
        }

        // After release, probe should succeed
        Assert.False(_sut.IsLockFileHeld());
    }

    [Fact]
    public void GetStatus_ReportsNotRunning_WhenNoPidFileAndNoLock()
    {
        var status = _sut.GetStatus();
        Assert.False(status.IsRunning);
        Assert.Null(status.Pid);
    }

    [Fact]
    public void GetStatus_ReportsRunning_WhenLockHeldButNoPidFile()
    {
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var status = _sut.GetStatus();
        Assert.True(status.IsRunning);
        Assert.Null(status.Pid);
        Assert.Contains("PID file missing", status.Message);
    }

    [Fact]
    public void Start_RefusesToStart_WhenLockHeld()
    {
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = _sut.Start();
        Assert.False(result.Success);
        Assert.Contains("already running", result.Message);
    }

    [Fact]
    public void Start_DoesNotSpawn_AndReportsManaged_WhenSupervised_AndDaemonRunning()
    {
        // A supervised daemon holds the lock; the CLI must defer to the supervisor
        // and report success rather than spawning a second netclawd (#1279).
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var supervised = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(true));

        var result = supervised.Start();

        Assert.True(result.Success);
        Assert.Contains("managed by container supervisor", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Start_ReportsSupervisorOwnsStartup_WhenSupervised_AndNotRunning()
    {
        // No lock held, no real netclawd binary: the non-supervised path would try
        // to find/spawn the binary. The supervised path must instead defer to the
        // supervisor and never reach the spawn logic.
        var supervised = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(true));

        var result = supervised.Start();

        Assert.False(result.Success);
        Assert.Contains("container supervisor", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot find netclawd", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopAsync_DefersToSupervisor_WhenSupervised()
    {
        // Symmetric with Start(): the CLI must not stop a supervised daemon — a SIGTERM
        // would just be undone by the supervisor restarting it (#1279).
        var supervised = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(true));

        var result = await supervised.StopAsync("cli-stop");

        Assert.False(result.Success);
        Assert.Contains("container supervisor", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docker stop", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeSupervisor(bool supervised) : IContainerSupervisor
    {
        public bool IsExternallySupervised => supervised;
    }

    public void Dispose()
    {
        try { _dir.Dispose(); }
        catch (IOException) { } // slopwatch-ignore: SW003 test cleanup best-effort — directory may already be gone
    }
}
