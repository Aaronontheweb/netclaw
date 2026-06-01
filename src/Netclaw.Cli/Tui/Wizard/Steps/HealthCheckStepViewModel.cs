// -----------------------------------------------------------------------
// <copyright file="HealthCheckStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using R3;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Final wizard step: runs health checks, writes config, starts daemon.
/// No sub-steps — the entire step is the finalization sequence.
/// </summary>
public sealed class HealthCheckStepViewModel : IWizardStepViewModel
{
    private static readonly TimeSpan OverallHealthCheckTimeout = TimeSpan.FromMinutes(5);

    // Generous enough to absorb a supervisor restart gap, including the
    // entrypoint's crash-loop backoff (caps at 60s) when the daemon was down
    // and the supervisor must (re)start it from the freshly-written config.
    private static readonly TimeSpan SupervisedReadyTimeout = TimeSpan.FromSeconds(90);

    private const string NotReadyMessage = "Daemon did not become ready (personality setup skipped)";

    private readonly DaemonManager? _daemonManager;
    private readonly DaemonApi? _daemonApi;
    private readonly ChatNavigationState? _navigationState;
    private readonly TimeProvider _timeProvider;
    private readonly IContainerSupervisor _supervisor;
    private WizardContext? _context;

    public HealthCheckStepViewModel(
        DaemonManager? daemonManager = null,
        DaemonApi? daemonApi = null,
        ChatNavigationState? navigationState = null,
        TimeProvider? timeProvider = null,
        IContainerSupervisor? supervisor = null)
    {
        _daemonManager = daemonManager;
        _daemonApi = daemonApi;
        _navigationState = navigationState;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _supervisor = supervisor ?? new ContainerSupervisor();
    }

    public string StepId => WizardStepIds.HealthCheck;
    public string DisplayTitle => "Health Check";

    // ── Reactive state ──
    public ReactiveProperty<bool> IsRunning { get; } = new(false);
    public ReactiveProperty<bool> IsComplete { get; } = new(false);
    public List<HealthCheckItem> Results { get; } = [];
    internal ReactiveProperty<int> ResultVersion { get; } = new(0);

    /// <summary>Task that completes when health check finishes. For testing.</summary>
    internal Task? HealthCheckCompletion { get; private set; }

    /// <summary>Navigate callback to transition to chat after success.</summary>
    public Action<string>? Navigate { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => 0;
    public int SubStepCount => 1;

    public string GetHelpText() => "  Validating your configuration...";

    public bool TryAdvance()
    {
        // Trigger health check on Enter
        if (!IsRunning.Value && !IsComplete.Value)
            HealthCheckCompletion = RunHealthCheckAsync();
        return true; // always handled internally (we don't advance past health check)
    }

    public bool TryGoBack() => false;

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;

        if (direction == NavigationDirection.Forward)
        {
            IsRunning.Value = false;
            IsComplete.Value = false;
            Results.Clear();
            NotifyChanged();
        }
    }

    public void OnLeave() { }

    // ── Health check does not contribute config — it writes config from all steps ──
    public void ContributeConfig(WizardConfigBuilder builder) { }
    public void ContributeSecrets(WizardSecretsBuilder builder) { }
    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Start the health check with orchestrator support. Sets <see cref="HealthCheckCompletion"/>
    /// and runs asynchronously.
    /// </summary>
    public void StartWithOrchestrator(WizardOrchestrator orchestrator)
    {
        HealthCheckCompletion = RunWithOrchestrator(orchestrator);
    }

    /// <summary>
    /// Run the full health check, write config, and start daemon.
    /// The <paramref name="orchestrator"/> is used to collect config from all steps.
    /// </summary>
    public async Task RunWithOrchestrator(WizardOrchestrator orchestrator)
    {
        using var overallCts = new CancellationTokenSource(OverallHealthCheckTimeout);
        try
        {
            await RunHealthCheckCoreAsync(orchestrator, overallCts.Token);
        }
        catch (OperationCanceledException) when (overallCts.IsCancellationRequested)
        {
            Results.Add(new HealthCheckItem("Health check timed out", false));
            IsRunning.Value = false;
            IsComplete.Value = true;
            NotifyChanged();
            if (_context is not null)
                _context.StatusMessage.Value = "Setup timed out. Run `netclaw daemon start` to begin.";
        }
    }

    private Task RunHealthCheckAsync()
    {
        // Standalone mode — no orchestrator. Used for testing.
        IsRunning.Value = true;
        IsComplete.Value = false;
        Results.Clear();
        NotifyChanged();

        IsRunning.Value = false;
        IsComplete.Value = true;
        NotifyChanged();
        return Task.CompletedTask;
    }

    private async Task RunHealthCheckCoreAsync(WizardOrchestrator orchestrator, CancellationToken ct)
    {
        IsRunning.Value = true;
        IsComplete.Value = false;
        Results.Clear();
        NotifyChanged();

        var runner = new HealthCheckRunner(Results, NotifyChanged);

        // Run health checks from all steps
        await orchestrator.RunHealthChecksAsync(runner, ct);

        // Stop daemon before writing config. Under a container supervisor we must
        // NOT stop the daemon: stopping it would make the supervisor (re)start it
        // — possibly with a backoff and before config is written. Instead we leave
        // it running and trigger an in-process restart after the write (#1279).
        if (_daemonManager is not null && !_supervisor.IsExternallySupervised)
        {
            var status = _daemonManager.GetStatus();
            if (status.IsRunning)
            {
                runner.Add(new HealthCheckItem("Stopping daemon for config update", null));
                var stopResult = await _daemonManager.StopAsync("config-update");
                runner.UpdateLast(stopResult.Success
                    ? new HealthCheckItem("Daemon stopped", true)
                    : new HealthCheckItem($"Daemon stop failed: {stopResult.Message}", false));
            }
        }

        // Write config
        runner.Add(new HealthCheckItem("Writing configuration", null));
        try
        {
            orchestrator.WriteConfig();

            // Write identity files from the identity step
            // (find it in the step list — it owns the identity file generation)

            runner.UpdateLast(new HealthCheckItem("Configuration written", true));
        }
        catch (Exception ex)
        {
            runner.UpdateLast(new HealthCheckItem($"Configuration write failed: {ex.Message}", false));
        }

        // Apply config if all passed. On host installs the CLI (re)starts a detached
        // daemon; under a container supervisor we instead ask the running daemon to
        // restart in-process so we never spawn a second netclawd (#1279).
        var allPassed = runner.AllPassed;
        if (allPassed)
        {
            var supervised = _supervisor.IsExternallySupervised;
            runner.Add(new HealthCheckItem(supervised ? "Applying configuration" : "Starting daemon", null));
            var daemonOk = supervised
                ? await RestartAndPollSupervisedAsync(ct)
                : await StartAndPollDaemonAsync(ct);
            if (daemonOk)
            {
                runner.UpdateLast(new HealthCheckItem("Daemon ready", true));
            }
            else if (Results.Count > 0 && Results[^1].Passed is null)
            {
                runner.UpdateLast(new HealthCheckItem(NotReadyMessage, false));
            }
        }

        IsRunning.Value = false;
        IsComplete.Value = true;
        NotifyChanged();

        allPassed = runner.AllPassed;
        if (allPassed && _context is not null)
        {
            _context.StatusMessage.Value = "Setup complete! Launching chat...";
            Navigate?.Invoke("/chat");
        }
        else if (_context is not null)
        {
            _context.StatusMessage.Value = "Setup complete with warnings. Run `netclaw daemon start` to begin.";
        }
    }

    private async Task<bool> StartAndPollDaemonAsync(CancellationToken ct)
    {
        if (_daemonManager is null) return false;

        // DaemonManager.Start only consults the crash log on its 1.5s WaitForExit
        // branch — anything that crashes after Start returns is invisible to it.
        var startedAt = _timeProvider.GetUtcNow();

        var result = _daemonManager.Start();
        if (!result.Success && !result.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
        {
            var failureText = result.CrashLogPath is null
                ? result.Message
                : $"{result.Message} See crash log: {result.CrashLogPath}";
            Results[^1] = new HealthCheckItem(failureText, false);
            NotifyChanged();
            return false;
        }

        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (_daemonApi is not null && await _daemonApi.IsHealthyAsync(ct))
                    return true;
            }
            catch (HttpRequestException)
            {
                Results[^1] = new HealthCheckItem($"Starting daemon ({i + 1}s)", null);
                NotifyChanged();
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Results[^1] = new HealthCheckItem($"Starting daemon ({i + 1}s)", null);
                NotifyChanged();
            }

            if (!_daemonManager.GetStatus().IsRunning)
                break;

            await Task.Delay(1000, ct);
        }

        var crashFailure = _daemonManager.TryReadStartupFailureFromCrashLog(startedAt, out var crashLogPath);
        var failureMessage = (crashFailure, crashLogPath) switch
        {
            (not null, _)  => $"{crashFailure} See crash log: {crashLogPath}",
            (null, not null) => $"{NotReadyMessage}. See crash log: {crashLogPath}",
            _ => null
        };
        if (failureMessage is not null)
        {
            Results[^1] = new HealthCheckItem(failureMessage, false);
            NotifyChanged();
        }

        return false;
    }

    /// <summary>
    /// Container-supervised finalization: asks the running daemon to restart
    /// in-process (best-effort) so it re-reads the freshly-written config, then
    /// polls readiness. Never spawns a daemon — the supervisor owns startup. If
    /// the daemon is unreachable (e.g. crash-looping on first boot before config
    /// existed) the supervisor restarts it from the on-disk config and the poll
    /// still observes it become ready.
    /// </summary>
    private async Task<bool> RestartAndPollSupervisedAsync(CancellationToken ct)
    {
        if (_daemonApi is null) return false;

        // Capture the daemon's restart generation (PID-file start time, which the
        // daemon advances on every in-process restart) BEFORE requesting the restart.
        // Both the draining pre-restart daemon and the restarted one answer the
        // anonymous /health/ready, so "healthy" alone is not proof the new config is
        // live — we must also see a newer generation.
        var beforeStartedAt = _daemonManager?.TryGetRecordedStartTime();

        // Window for crash-log diagnostics if the restart never becomes ready.
        var restartRequestedAt = _timeProvider.GetUtcNow();

        var daemonReachable = true;
        var restartAccepted = false;
        try
        {
            restartAccepted = await _daemonApi.RestartAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Daemon not reachable (e.g. crash-looping on first boot before config
            // existed). The supervisor will start it from the on-disk config; poll below.
            daemonReachable = false;
            Results[^1] = new HealthCheckItem("Applying configuration (waiting for supervisor)", null);
            NotifyChanged();
        }

        // A reachable daemon that REJECTED the restart (e.g. 401, or the coordinator
        // declined) will keep running the old config. Surface it instead of polling
        // the still-running old daemon and falsely reporting success.
        if (daemonReachable && !restartAccepted)
        {
            Results[^1] = new HealthCheckItem(
                "Daemon rejected the restart request — configuration not applied.", false);
            NotifyChanged();
            return false;
        }

        // Poll until a NEWER daemon generation is healthy. Unlike the host path we
        // never break early on "not running": the daemon goes down then comes back as
        // the supervisor's child, possibly after a restart backoff.
        var deadline = _timeProvider.GetUtcNow() + SupervisedReadyTimeout;
        var elapsedSeconds = 0;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();

            bool ready;
            try
            {
                ready = await _daemonApi.IsHealthyAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                ready = false; // daemon mid-restart / per-request timeout — keep waiting
            }

            if (ready && IsRestartedGeneration(beforeStartedAt))
                return true;

            Results[^1] = new HealthCheckItem($"Applying configuration ({++elapsedSeconds}s)", null);
            NotifyChanged();
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, ct);
        }

        // Timed out: surface the same startup-abort diagnostic the host path does, so a
        // first-boot bad-config crash-loop isn't reported as a generic "not ready".
        if (_daemonManager is not null)
        {
            var crashFailure = _daemonManager.TryReadStartupFailureFromCrashLog(restartRequestedAt, out var crashLogPath);
            var failureMessage = (crashFailure, crashLogPath) switch
            {
                (not null, _) => $"{crashFailure} See crash log: {crashLogPath}",
                (null, not null) => $"{NotReadyMessage}. See crash log: {crashLogPath}",
                _ => null
            };
            if (failureMessage is not null)
            {
                Results[^1] = new HealthCheckItem(failureMessage, false);
                NotifyChanged();
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the daemon now reports a newer restart generation than
    /// <paramref name="before"/> (the PID-file start time advances on each in-process
    /// restart). A missing pre-restart value (daemon was down) means any live instance
    /// qualifies; a missing current value means the restarted daemon hasn't written its
    /// PID file yet, so it does not yet qualify. Without a daemon manager there is no
    /// generation source to confirm a restart, so we fail safe (treat as not-yet-restarted)
    /// rather than risk reporting the still-draining old daemon as ready.
    /// </summary>
    internal bool IsRestartedGeneration(DateTimeOffset? before)
    {
        if (_daemonManager is null) return false;
        var current = _daemonManager.TryGetRecordedStartTime();
        if (current is null) return false;
        return before is null || current > before;
    }

    private void NotifyChanged()
    {
        ResultVersion.Value++;
        _context?.RequestRedraw();
    }

    public void Dispose()
    {
        IsRunning.Dispose();
        IsComplete.Dispose();
        ResultVersion.Dispose();
    }
}
