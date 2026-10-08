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
    private readonly List<System.Diagnostics.Process> _fakeDaemons = [];

    public DaemonManagerSingletonGuardTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        // Pin non-supervised so these assertions don't depend on the ambient
        // NETCLAW_CONTAINER_SUPERVISOR env var (which the official image sets, and which
        // would otherwise flip the default ContainerSupervisor when running in-image).
        _sut = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(false));
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
    public async Task StopAsync_ProceedsToStopTheProcess_WhenSupervised()
    {
        // `netclaw daemon stop` is the only CLI way to bounce a containerised daemon: the
        // supervisor restarts it after the exit. Stop must act, not refuse: with the lock held and
        // no usable PID it reaches the same "PID file is missing" outcome as an unsupervised stop.
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var supervised = new DaemonManager(_paths, TimeProvider.System, new FakeSupervisor(true));

        var result = await supervised.StopAsync("cli-stop", CancellationToken.None);

        Assert.Contains("PID file is missing", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("supervisor", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public void GetStatus_TreatsAPidFileAsStale_WhenThisHomesLockIsFree_EvenIfTheProcessIsAlive()
    {
        // F4: another home's pid file can name the unit's live daemon. Without this home's lock
        // held, that pid is not this home's daemon.
        var daemon = StartFakeDaemon();
        File.WriteAllText(_paths.PidFilePath, daemon.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var status = _sut.GetStatus();

        Assert.False(status.IsRunning);
        Assert.Null(status.Pid);
        Assert.False(File.Exists(_paths.PidFilePath));
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public void GetStatus_ReportsThePidFilesProcess_WhenThisHomesLockIsHeld()
    {
        var daemon = StartFakeDaemon();
        File.WriteAllText(_paths.PidFilePath, daemon.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var holder = new FileStream(
            _paths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var status = _sut.GetStatus();

        Assert.True(status.IsRunning);
        Assert.Equal(daemon.Id, status.Pid);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private System.Diagnostics.Process StartFakeDaemon()
    {
        var fakeDaemon = Path.Combine(_dir.Path, "netclawd");
        File.Copy("/bin/sleep", fakeDaemon, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fakeDaemon, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fakeDaemon, "600") { UseShellExecute = false })!;
        _fakeDaemons.Add(process);
        return process;
    }

    private sealed class FakeSupervisor(bool supervised) : IContainerSupervisor
    {
        public bool IsExternallySupervised => supervised;
    }

    public void Dispose()
    {
        foreach (var process in _fakeDaemons)
        {
            if (!process.HasExited)
                process.Kill();
            process.Dispose();
        }

        try { _dir.Dispose(); }
        catch (IOException) { } // slopwatch-ignore: SW003 test cleanup best-effort — directory may already be gone
    }
}
