// -----------------------------------------------------------------------
// <copyright file="SystemdUserServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class SystemdUserServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    private static readonly DaemonStatus NotRunning = new(false, null, "Daemon is not running.");

    private static DaemonStatus RunningAs(int pid) => new(true, pid, $"Daemon running (PID {pid}).");

    private SystemdUserService ServiceFor(FakeSystemCommandRunner runner, string homePath)
        => new(WriteUnit(), runner, enabledOnThisPlatform: true, homePath: homePath);

    private string ScratchHome => Path.Combine(_dir.Path, "scratch-home");

    private static SystemCommandResult State(string state) =>
        new(state == "active" || state == "reloading" ? 0 : 3, string.Empty, StandardOutput: state + "\n");

    private static SystemCommandResult MainPid(string value) => new(0, string.Empty, StandardOutput: value);

    private static readonly string DefaultHome = SystemdUserService.DefaultHomePath;

    private static void AssertKind(SystemdUserServiceOwnershipKind expected, SystemdUserServiceOwnership actual) =>
        Assert.Equal(expected, actual.Kind);

    // ---- stop rule ----

    [Fact]
    public async Task Stop_ReturnsUnmanaged_WhenUnitFileMissing()
    {
        var runner = new FakeSystemCommandRunner();
        var service = new SystemdUserService(
            Path.Combine(_dir.Path, "missing.service"), runner, enabledOnThisPlatform: true, homePath: DefaultHome);

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, await service.GetStopOwnershipAsync(NotRunning));
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData("active", "4242")]
    [InlineData("reloading", "4242")]
    public async Task Stop_ReturnsManaged_WhenTheUnitsMainPidIsThisHomesDaemon_WhateverTheHomePath(string state, string mainPid)
    {
        foreach (var home in new[] { DefaultHome, ScratchHome })
        {
            var runner = new FakeSystemCommandRunner();
            runner.Enqueue(State(state));
            runner.Enqueue(MainPid(mainPid));

            AssertKind(SystemdUserServiceOwnershipKind.Managed, await ServiceFor(runner, home).GetStopOwnershipAsync(RunningAs(4242)));
        }
    }

    [Fact]
    public async Task Stop_ReturnsManaged_ForACrashLoopingUnitOnTheDefaultHome_WithNoDaemonRunning()
    {
        // F1: activating, MainPID=0 (auto-restart wait), nothing running: the unit will start a daemon.
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("activating"));
        runner.Enqueue(MainPid("0\n"));

        AssertKind(SystemdUserServiceOwnershipKind.Managed, await ServiceFor(runner, DefaultHome).GetStopOwnershipAsync(NotRunning));
    }

    [Fact]
    public async Task Stop_ReturnsManaged_ForAnActivatingUnitWithAFreshMainPid_OnTheDefaultHome()
    {
        // The unit's process is still starting (or about to crash): it is not another home's settled daemon.
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("activating"));
        runner.Enqueue(MainPid("777\n"));

        AssertKind(SystemdUserServiceOwnershipKind.Managed, await ServiceFor(runner, DefaultHome).GetStopOwnershipAsync(NotRunning));
    }

    [Fact]
    public async Task Stop_ReturnsManaged_WhenADetachedDaemonHoldsTheDefaultHomeAndTheUnitLoopsOnTheLock()
    {
        // F2: detached daemon 4242, unit activating (MainPID 0 between restarts).
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("activating"));
        runner.Enqueue(MainPid("0\n"));

        AssertKind(SystemdUserServiceOwnershipKind.Managed, await ServiceFor(runner, DefaultHome).GetStopOwnershipAsync(RunningAs(4242)));
    }

    [Fact]
    public async Task Stop_ReturnsUnmanaged_WhenTheDefaultHomeHasNoDaemonAndTheUnitServesAnotherHome()
    {
        // Drop-in NETCLAW_HOME=/home/op/data: the unit is active with a settled daemon (not ours).
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("active"));
        runner.Enqueue(MainPid("9999\n"));

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, await ServiceFor(runner, DefaultHome).GetStopOwnershipAsync(NotRunning));
    }

    [Fact]
    public async Task Stop_ReturnsUnmanaged_ForAScratchHomeWhoseDaemonIsNotTheUnitsMainPid()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("active"));
        runner.Enqueue(MainPid("9999\n"));

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, await ServiceFor(runner, ScratchHome).GetStopOwnershipAsync(RunningAs(4242)));
    }

    [Fact]
    public async Task Stop_ReturnsUnmanaged_ForAScratchHomeWithNoDaemon_WithoutAskingSystemd()
    {
        var runner = new FakeSystemCommandRunner();

        var ownership = await ServiceFor(runner, ScratchHome).GetStopOwnershipAsync(NotRunning);

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, ownership);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Stop_ReturnsUnmanaged_ForAScratchHomeEvenWhenTheUnitIsActivatingWithMainPidZero()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("activating"));
        runner.Enqueue(MainPid("0\n"));

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, await ServiceFor(runner, ScratchHome).GetStopOwnershipAsync(RunningAs(4242)));
    }

    [Fact]
    public async Task Stop_DoesNotTreatAnUnknownDaemonPidAsAMainPidMatch()
    {
        // Pid unknown (lock held, no pid file) must not equal MainPID=0 and "match".
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("active"));
        runner.Enqueue(MainPid("0\n"));

        var ownership = await ServiceFor(runner, ScratchHome)
            .GetStopOwnershipAsync(new DaemonStatus(true, null, "Daemon is running (PID file missing)."));

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, ownership);
    }

    [Fact]
    public async Task Stop_DoesNotTreatAMainPidOfZeroAsAMatch_ForADaemonWithPidZero()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("active"));
        runner.Enqueue(MainPid("0\n"));

        var ownership = await ServiceFor(runner, ScratchHome).GetStopOwnershipAsync(RunningAs(0));

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, ownership);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("failed")]
    [InlineData("deactivating")]
    public async Task Stop_ReturnsUnmanaged_WhenTheUnitCannotStartADaemon_AndNeverReadsMainPid(string state)
    {
        // deactivating: the unit's own ExecStop is calling us; stopping the unit again would wait on itself.
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State(state));

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, await ServiceFor(runner, DefaultHome).GetStopOwnershipAsync(RunningAs(4242)));
        Assert.Equal([("systemctl", "--user is-active netclaw.service")], runner.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_ReturnsUnknown_AndNamesTheUnreachableBus_WhenSystemctlCannotAnswer(bool daemonRunning)
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(1, "Failed to connect to bus: No medium found"));

        var ownership = await ServiceFor(runner, DefaultHome).GetStopOwnershipAsync(daemonRunning ? RunningAs(4242) : NotRunning);

        AssertKind(SystemdUserServiceOwnershipKind.Unknown, ownership);
        Assert.Contains("systemd unit is installed but the user session bus is not reachable", ownership.Message, StringComparison.Ordinal);
        Assert.Contains("login session", ownership.Message, StringComparison.Ordinal);
        Assert.Contains("systemctl --user", ownership.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_ReturnsManaged_ForTheDefaultHomeWrittenWithATrailingSlash()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("activating"));
        runner.Enqueue(MainPid("0\n"));

        AssertKind(SystemdUserServiceOwnershipKind.Managed,
            await ServiceFor(runner, DefaultHome + Path.DirectorySeparatorChar).GetStopOwnershipAsync(NotRunning));
    }

    // ---- start rule ----

    [Fact]
    public async Task Start_ReturnsManaged_WhenTheDefaultHomesUnitIsActive()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));

        AssertKind(SystemdUserServiceOwnershipKind.Managed, await ServiceFor(runner, DefaultHome).GetStartOwnershipAsync());
        Assert.Equal([("systemctl", "--user is-active netclaw.service")], runner.Commands);
    }

    [Fact]
    public async Task Start_ReturnsManaged_WhenTheDefaultHomesUnitIsEnabledButInactive()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(3, string.Empty));
        runner.Enqueue(new SystemCommandResult(0, string.Empty));

        AssertKind(SystemdUserServiceOwnershipKind.Managed, await ServiceFor(runner, DefaultHome).GetStartOwnershipAsync());
        Assert.Equal(
            [
                ("systemctl", "--user is-active netclaw.service"),
                ("systemctl", "--user is-enabled --quiet netclaw.service")
            ],
            runner.Commands);
    }

    [Fact]
    public async Task Start_ReturnsUnmanaged_WhenTheUnitIsAlreadyStopping()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(State("deactivating"));

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, await ServiceFor(runner, DefaultHome).GetStartOwnershipAsync());
    }

    [Fact]
    public async Task Start_ReturnsUnmanaged_ForAHomeOtherThanTheDefault_WithoutAskingSystemd()
    {
        var runner = new FakeSystemCommandRunner();

        var ownership = await ServiceFor(runner, ScratchHome).GetStartOwnershipAsync();

        AssertKind(SystemdUserServiceOwnershipKind.Unmanaged, ownership);
        Assert.Contains("default home", ownership.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Start_ReturnsUnknown_WhenSystemctlCannotAnswer()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(1, "Failed to connect to bus: No medium found"));
        runner.Enqueue(new SystemCommandResult(1, string.Empty));

        AssertKind(SystemdUserServiceOwnershipKind.Unknown, await ServiceFor(runner, DefaultHome).GetStartOwnershipAsync());
    }

    [Fact]
    public async Task StartAndStop_RunSystemctlUserCommands()
    {
        var unitPath = WriteUnit();
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        var service = new SystemdUserService(unitPath, runner, enabledOnThisPlatform: true, homePath: SystemdUserService.DefaultHomePath);

        var stop = await service.StopAsync();
        var start = await service.StartAsync();

        Assert.True(stop.Success);
        Assert.True(start.Success);
        Assert.Equal(
            [
                ("systemctl", "--user stop netclaw.service"),
                ("systemctl", "--user start netclaw.service")
            ],
            runner.Commands);
    }

    private string WriteUnit()
    {
        var unitPath = Path.Combine(_dir.Path, "netclaw.service");
        File.WriteAllText(unitPath, "[Service]\nExecStart=/opt/netclaw/netclawd\n");
        return unitPath;
    }

    public void Dispose() => _dir.Dispose();

    private sealed class FakeSystemCommandRunner : ISystemCommandRunner
    {
        private readonly Queue<SystemCommandResult> _results = [];

        public List<(string Command, string Arguments)> Commands { get; } = [];

        public void Enqueue(SystemCommandResult result) => _results.Enqueue(result);

        public Task<SystemCommandResult> RunAsync(string command, string arguments)
        {
            Commands.Add((command, arguments));
            return Task.FromResult(_results.Count == 0
                ? new SystemCommandResult(1, string.Empty)
                : _results.Dequeue());
        }
    }
}
