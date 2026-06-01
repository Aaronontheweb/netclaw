// -----------------------------------------------------------------------
// <copyright file="HealthCheckStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class HealthCheckStepViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public HealthCheckStepViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", null);
        _dir.Dispose();
    }

    [Fact]
    public async Task RunWithOrchestrator_PreservesSpecificStartupFailureMessage()
    {
        var fakeBinaryPath = Path.Combine(
            _dir.Path,
            OperatingSystem.IsWindows() ? "fake-netclawd.cmd" : "fake-netclawd.sh");
        await File.WriteAllTextAsync(
            fakeBinaryPath,
            OperatingSystem.IsWindows()
                ? "@echo off\r\nexit /b 1\r\n"
                : "#!/usr/bin/env bash\nexit 1\n",
            TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fakeBinaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", fakeBinaryPath);

        var expectedMessage =
            "Daemon startup aborted: Invalid reverse-proxy topology: Daemon.Host '127.0.0.1' is loopback.";
        var crashLogPath = Path.Combine(_paths.LogsDirectory, "crash-test.log");
        await File.WriteAllTextAsync(
            crashLogPath,
            $"{expectedMessage}{Environment.NewLine}",
            TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(crashLogPath, DateTime.UtcNow.AddMinutes(1));

        var daemonManager = new DaemonManager(_paths, TimeProvider.System);
        using var step = new HealthCheckStepViewModel(
            daemonManager,
            daemonApi: null,
            navigationState: new ChatNavigationState());
        using var exposureStep = new ExposureModeStepViewModel
        {
            SelectedMode = ExposureMode.ReverseProxy
        };

        using var context = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };

        step.OnEnter(context, NavigationDirection.Forward);
        exposureStep.OnEnter(context, NavigationDirection.Forward);

        using var orchestrator = new WizardOrchestrator([exposureStep, step], context);

        await step.RunWithOrchestrator(orchestrator);

        Assert.NotEmpty(step.Results);
        var failure = Assert.Single(step.Results, result => result.Passed is false);
        Assert.Contains(expectedMessage, failure.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Daemon did not become ready", failure.Label, StringComparison.Ordinal);
        Assert.Contains(crashLogPath, failure.Label, StringComparison.Ordinal);
        Assert.Equal("Setup complete with warnings. Run `netclaw daemon start` to begin.", context.StatusMessage.Value);
    }

    [Fact]
    public async Task OnEnter_Forward_AfterFailedRun_ResetsStateForRetry()
    {
        using var step = new HealthCheckStepViewModel(
            daemonManager: null,
            daemonApi: null,
            navigationState: new ChatNavigationState());
        using var exposureStep = new ExposureModeStepViewModel
        {
            SelectedMode = ExposureMode.Local
        };

        using var context = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };

        step.OnEnter(context, NavigationDirection.Forward);
        exposureStep.OnEnter(context, NavigationDirection.Forward);

        using var orchestrator = new WizardOrchestrator([exposureStep, step], context);

        // First run — daemon start fails because DaemonManager is null
        await step.RunWithOrchestrator(orchestrator);
        Assert.True(step.IsComplete.Value);
        Assert.False(step.IsRunning.Value);

        // Simulate going back and re-entering
        step.OnEnter(context, NavigationDirection.Forward);

        Assert.False(step.IsComplete.Value);
        Assert.False(step.IsRunning.Value);
        Assert.Empty(step.Results);

        // Second run should execute (not blocked by stale IsComplete)
        await step.RunWithOrchestrator(orchestrator);
        Assert.True(step.IsComplete.Value);
    }

    // Restart-generation timestamps written to the PID file. The wizard treats a
    // newer value as proof the daemon actually restarted onto the new config.
    private static readonly DateTimeOffset OldGeneration = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NewGeneration = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunWithOrchestrator_RunningDaemon_PollsUntilReloaded_AndReportsReady()
    {
        // A running daemon reloads in-process when config is written (its
        // ConfigWatcherService restarts it). The wizard must NOT stop it and must NOT
        // POST a restart; it just writes config and waits for a NEWER restart generation
        // — not merely a healthy probe, which the still-draining old daemon answers too (#1279).
        //
        // Hold the lock file so DaemonManager.GetStatus() reports the daemon as running
        // (lock held → running) without a real netclawd process.
        using var lockHolder = new FileStream(
            _paths.LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var daemonManager = new DaemonManager(_paths, TimeProvider.System);

        // Fake daemon: healthy, and — simulating the in-process reload restart — stamps a
        // fresh PID-file generation the first time readiness is probed.
        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/health/ready")
                WritePidFile(pid: 4321, startedAt: NewGeneration);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var daemonApi = new DaemonApi(new StubHttpClientFactory(handler), new ConfigurationBuilder().Build(), _paths);

        using var step = new HealthCheckStepViewModel(
            daemonManager,
            daemonApi,
            navigationState: new ChatNavigationState(),
            timeProvider: TimeProvider.System);
        using var exposureStep = new ExposureModeStepViewModel { SelectedMode = ExposureMode.Local };
        using var context = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };

        step.OnEnter(context, NavigationDirection.Forward);
        exposureStep.OnEnter(context, NavigationDirection.Forward);
        using var orchestrator = new WizardOrchestrator([exposureStep, step], context);

        await step.RunWithOrchestrator(orchestrator);

        Assert.True(File.Exists(_paths.NetclawConfigPath));
        Assert.Contains(step.Results, r => r.Label == "Daemon ready" && r.Passed == true);
        // Watcher-owned: the wizard never stops the daemon and never triggers the restart itself.
        Assert.DoesNotContain(step.Results, r => r.Label.Contains("Stopping daemon", StringComparison.Ordinal));
        Assert.DoesNotContain("POST /api/lifecycle/restart", handler.Requests);
    }

    [Fact]
    public void IsRestartedGeneration_BlocksStale_AllowsNewerOrDownDaemon()
    {
        // The generation gate is what prevents the readiness race: a healthy probe
        // against the still-draining pre-restart daemon must NOT count as ready (#1279).
        WritePidFile(pid: 4321, startedAt: OldGeneration); // current recorded generation
        var daemonManager = new DaemonManager(_paths, TimeProvider.System);
        using var step = new HealthCheckStepViewModel(daemonManager);

        // Same generation as before the restart → daemon hasn't restarted → not ready.
        Assert.False(step.IsRestartedGeneration(OldGeneration));
        // Recorded generation is newer than the pre-restart value → restarted → ready.
        Assert.True(step.IsRestartedGeneration(OldGeneration.AddMinutes(-1)));
        // Daemon was down before (no pre-restart generation) → any live instance counts.
        Assert.True(step.IsRestartedGeneration(null));
    }

    private void WritePidFile(int pid, DateTimeOffset startedAt) =>
        File.WriteAllText(_paths.PidFilePath, $"{pid}\n{startedAt:O}");

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(responder(request));
        }
    }
}
