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

    [Fact]
    public async Task GetOwnershipAsync_ReturnsUnmanaged_WhenUnitFileMissing()
    {
        var runner = new FakeSystemCommandRunner();
        var service = new SystemdUserService(
            Path.Combine(_dir.Path, "missing.service"),
            runner,
            enabledOnThisPlatform: true, homePath: SystemdUserService.DefaultHomePath);

        var ownership = await service.GetOwnershipAsync(NotRunning);

        Assert.Equal(SystemdUserServiceOwnershipKind.Unmanaged, ownership.Kind);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsManaged_WhenNoDaemonRunsAndTheDefaultHomesUnitIsActive()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));

        var ownership = await ServiceFor(runner, SystemdUserService.DefaultHomePath).GetOwnershipAsync(NotRunning);

        Assert.Equal(SystemdUserServiceOwnershipKind.Managed, ownership.Kind);
        Assert.Equal([("systemctl", "--user is-active netclaw.service")], runner.Commands);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsManaged_WhenNoDaemonRunsAndTheDefaultHomesUnitIsEnabledButInactive()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(3, string.Empty));
        runner.Enqueue(new SystemCommandResult(0, string.Empty));

        var ownership = await ServiceFor(runner, SystemdUserService.DefaultHomePath).GetOwnershipAsync(NotRunning);

        Assert.Equal(SystemdUserServiceOwnershipKind.Managed, ownership.Kind);
        Assert.Equal(
            [
                ("systemctl", "--user is-active netclaw.service"),
                ("systemctl", "--user is-enabled --quiet netclaw.service")
            ],
            runner.Commands);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsUnmanaged_WhenTheUnitIsAlreadyStopping()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(3, string.Empty, StandardOutput: "deactivating\n"));

        var ownership = await ServiceFor(runner, SystemdUserService.DefaultHomePath).GetOwnershipAsync(RunningAs(4242));

        Assert.Equal(SystemdUserServiceOwnershipKind.Unmanaged, ownership.Kind);
        Assert.Equal([("systemctl", "--user is-active netclaw.service")], runner.Commands);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsUnknown_AndNamesTheUnreachableBus_WhenSystemctlCannotAnswer()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(1, "Failed to connect to bus: No medium found"));
        runner.Enqueue(new SystemCommandResult(1, string.Empty));

        var ownership = await ServiceFor(runner, SystemdUserService.DefaultHomePath).GetOwnershipAsync(NotRunning);

        Assert.Equal(SystemdUserServiceOwnershipKind.Unknown, ownership.Kind);
        Assert.Contains("Could not determine", ownership.Message, StringComparison.Ordinal);
        Assert.Contains("systemd unit is installed but the user session bus is not reachable", ownership.Message, StringComparison.Ordinal);
        Assert.Contains("login session", ownership.Message, StringComparison.Ordinal);
        Assert.Contains("systemctl --user", ownership.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsUnknown_ForARunningDaemon_WhenSystemctlCannotAnswer()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(1, "Failed to connect to bus: No medium found"));

        var ownership = await ServiceFor(runner, ScratchHome).GetOwnershipAsync(RunningAs(4242));

        Assert.Equal(SystemdUserServiceOwnershipKind.Unknown, ownership.Kind);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsUnmanaged_WhenNoDaemonRunsForAHomeOtherThanTheDefault_WithoutAskingSystemd()
    {
        var runner = new FakeSystemCommandRunner();

        var ownership = await ServiceFor(runner, ScratchHome).GetOwnershipAsync(NotRunning);

        Assert.Equal(SystemdUserServiceOwnershipKind.Unmanaged, ownership.Kind);
        Assert.Contains("default home", ownership.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsManaged_ForTheDefaultHomeWrittenWithATrailingSlash()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));

        var ownership = await ServiceFor(runner, SystemdUserService.DefaultHomePath + Path.DirectorySeparatorChar)
            .GetOwnershipAsync(NotRunning);

        Assert.Equal(SystemdUserServiceOwnershipKind.Managed, ownership.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetOwnershipAsync_ReturnsManaged_WhenTheUnitsMainPidIsThisHomesDaemon_WhateverTheHomePath(bool defaultHome)
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        runner.Enqueue(new SystemCommandResult(0, string.Empty, StandardOutput: "4242\n"));

        var ownership = await ServiceFor(runner, defaultHome ? SystemdUserService.DefaultHomePath : ScratchHome)
            .GetOwnershipAsync(RunningAs(4242));

        Assert.Equal(SystemdUserServiceOwnershipKind.Managed, ownership.Kind);
        Assert.Equal(
            [
                ("systemctl", "--user is-active netclaw.service"),
                ("systemctl", "--user show netclaw.service -p MainPID --value")
            ],
            runner.Commands);
    }

    [Theory]
    [InlineData("9999\n")]
    [InlineData("0\n")]
    [InlineData("")]
    public async Task GetOwnershipAsync_ReturnsUnmanaged_WhenTheUnitRunsAnotherDaemon_EvenForTheDefaultHome(string mainPid)
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        runner.Enqueue(new SystemCommandResult(0, string.Empty, StandardOutput: mainPid));

        var ownership = await ServiceFor(runner, SystemdUserService.DefaultHomePath).GetOwnershipAsync(RunningAs(4242));

        Assert.Equal(SystemdUserServiceOwnershipKind.Unmanaged, ownership.Kind);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsUnmanaged_ForARunningDaemon_WhenTheUnitIsNotActive()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(3, string.Empty, StandardOutput: "inactive\n"));

        var ownership = await ServiceFor(runner, SystemdUserService.DefaultHomePath).GetOwnershipAsync(RunningAs(4242));

        Assert.Equal(SystemdUserServiceOwnershipKind.Unmanaged, ownership.Kind);
        Assert.Equal([("systemctl", "--user is-active netclaw.service")], runner.Commands);
    }

    [Fact]
    public async Task GetOwnershipAsync_ReturnsUnmanaged_ForARunningDaemonWhosePidIsUnknown()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));

        var ownership = await ServiceFor(runner, SystemdUserService.DefaultHomePath)
            .GetOwnershipAsync(new DaemonStatus(true, null, "Daemon is running (PID file missing)."));

        Assert.Equal(SystemdUserServiceOwnershipKind.Unmanaged, ownership.Kind);
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
