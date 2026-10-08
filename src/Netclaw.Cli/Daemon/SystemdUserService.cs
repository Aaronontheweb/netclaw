// -----------------------------------------------------------------------
// <copyright file="SystemdUserService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

internal enum SystemdUserServiceOwnershipKind
{
    Unmanaged,
    Managed,
    Unknown
}

internal sealed record SystemdUserServiceOwnership(
    SystemdUserServiceOwnershipKind Kind,
    string Message)
{
    public static SystemdUserServiceOwnership Managed(string message) =>
        new(SystemdUserServiceOwnershipKind.Managed, message);

    public static SystemdUserServiceOwnership Unmanaged(string message) =>
        new(SystemdUserServiceOwnershipKind.Unmanaged, message);

    public static SystemdUserServiceOwnership Unknown(string message) =>
        new(SystemdUserServiceOwnershipKind.Unknown, message);
}

internal sealed class SystemdUserService(
    string? unitFilePath = null,
    ISystemCommandRunner? commandRunner = null,
    bool? enabledOnThisPlatform = null,
    string? homePath = null)
{
    private const string ServiceName = "netclaw.service";

    private readonly string _unitFilePath = unitFilePath ?? DaemonManager.SystemdUserUnitFilePath;
    private readonly ISystemCommandRunner _commandRunner = commandRunner ?? ProcessSystemCommandRunner.Instance;
    private readonly bool _enabledOnThisPlatform = enabledOnThisPlatform ?? OperatingSystem.IsLinux();

    private readonly string _homePath = homePath ?? new NetclawPaths().BasePath;

    /// <summary>
    /// Stop rule: after a stop succeeds, nothing may bring this home's daemon back. The unit is
    /// <see cref="SystemdUserServiceOwnershipKind.Managed"/> (so it must be stopped) when it is
    /// running or about to run a daemon (<c>active</c>, <c>activating</c> including auto-restart,
    /// <c>reloading</c>) and serves this home: its MainPID is this home's daemon, or this is the
    /// default home (links resolved) and the unit has no settled daemon of its own to point at
    /// (MainPID 0, or still <c>activating</c>). Any other home is never the unit's to stop on a
    /// guess. <c>deactivating</c> is left alone: that is the unit's own ExecStop re-entering.
    /// </summary>
    public async Task<SystemdUserServiceOwnership> GetStopOwnershipAsync(DaemonStatus daemonStatus)
    {
        if (PlatformOrUnitMissing() is { } skipped)
            return skipped;

        // Another home with nothing running for it: the unit cannot be what starts it.
        if (!daemonStatus.IsRunning && !IsDefaultHome(_homePath))
            return SystemdUserServiceOwnership.Unmanaged(
                $"No daemon is running for {_homePath}, and netclaw.service starts only the default home ({DefaultHomePath}).");

        var active = await _commandRunner.RunAsync("systemctl", $"--user is-active {ServiceName}");
        var state = active.StandardOutput.Trim();
        if (state is not ("active" or "activating" or "reloading"))
            return StateCheckFailed(active) ?? SystemdUserServiceOwnership.Unmanaged(
                $"netclaw.service is {(state.Length == 0 ? "not running" : state)}, so it will not start a daemon.");

        var show = await _commandRunner.RunAsync("systemctl", $"--user show {ServiceName} -p MainPID --value");
        var mainPid = show.Success && int.TryParse(show.StandardOutput.Trim(), out var parsed) ? parsed : 0;

        if (mainPid != 0 && daemonStatus.Pid == mainPid)
            return SystemdUserServiceOwnership.Managed($"netclaw.service runs this home's daemon (PID {mainPid}).");

        if ((mainPid == 0 || state == "activating") && IsDefaultHome(_homePath))
            return SystemdUserServiceOwnership.Managed($"netclaw.service is {state} and serves the default home.");

        return SystemdUserServiceOwnership.Unmanaged(
            $"netclaw.service does not serve {_homePath} (main PID {mainPid}).");
    }

    /// <summary>
    /// Start rule, consulted only when no daemon runs for this home: the default home (links
    /// resolved) is started through the unit when it is active or enabled; any other home is not.
    /// </summary>
    public async Task<SystemdUserServiceOwnership> GetStartOwnershipAsync()
    {
        if (PlatformOrUnitMissing() is { } skipped)
            return skipped;

        if (!IsDefaultHome(_homePath))
            return SystemdUserServiceOwnership.Unmanaged(
                $"netclaw.service starts only the default home ({DefaultHomePath}), not {_homePath}.");

        var active = await _commandRunner.RunAsync("systemctl", $"--user is-active {ServiceName}");

        // The unit's ExecStop= runs `netclaw daemon stop`, so that process is part of a stop job
        // that systemd already started. Asking systemd to start the unit now would queue behind it.
        if (!active.Success && active.StandardOutput.Trim() == "deactivating")
            return SystemdUserServiceOwnership.Unmanaged("netclaw.service is already stopping.");

        if (active.Success)
            return SystemdUserServiceOwnership.Managed("netclaw.service is active.");

        var enabled = await _commandRunner.RunAsync("systemctl", $"--user is-enabled --quiet {ServiceName}");
        if (enabled.Success)
            return SystemdUserServiceOwnership.Managed("netclaw.service is enabled.");

        return StateCheckFailed(active, enabled) ?? SystemdUserServiceOwnership.Unmanaged(
            "netclaw.service is installed but neither active nor enabled.");
    }

    private SystemdUserServiceOwnership? PlatformOrUnitMissing()
    {
        if (!_enabledOnThisPlatform)
            return SystemdUserServiceOwnership.Unmanaged("systemd user services are Linux-only.");

        return File.Exists(_unitFilePath)
            ? null
            : SystemdUserServiceOwnership.Unmanaged("No netclaw systemd user service is installed.");
    }

    private static SystemdUserServiceOwnership? StateCheckFailed(SystemCommandResult active, SystemCommandResult? enabled = null)
    {
        if (active.ExecutionError is not null || enabled?.ExecutionError is not null)
        {
            return SystemdUserServiceOwnership.Unknown(
                $"Could not execute systemctl: {active.ExecutionError ?? enabled?.ExecutionError}");
        }

        if (!string.IsNullOrWhiteSpace(active.StandardError) || !string.IsNullOrWhiteSpace(enabled?.StandardError))
        {
            return SystemdUserServiceOwnership.Unknown(
                $"Could not determine netclaw.service state: {active.Message}" +
                (enabled is null ? string.Empty : $"; {enabled.Message}") +
                ". A systemd unit is installed but the user session bus is not reachable: " +
                "run this command from a login session, or use `systemctl --user` directly.");
        }

        return null;
    }

    internal static string DefaultHomePath => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".netclaw"));

    private static bool IsDefaultHome(string homePath) =>
        string.Equals(ResolveLinks(homePath), ResolveLinks(DefaultHomePath), StringComparison.Ordinal);

    private static string ResolveLinks(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            var target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
        }
        catch (IOException)
        {
            return full; // not a readable link: compare the path as spelled
        }

        return full;
    }

    public async Task<DaemonResult> StopAsync()
    {
        var result = await _commandRunner.RunAsync("systemctl", $"--user stop {ServiceName}");
        return ToDaemonResult(result, "Stopped systemd user service.");
    }

    public async Task<DaemonResult> StartAsync()
    {
        var result = await _commandRunner.RunAsync("systemctl", $"--user start {ServiceName}");
        return ToDaemonResult(result, "Started systemd user service.");
    }

    private static DaemonResult ToDaemonResult(SystemCommandResult result, string successMessage) =>
        result.Success
            ? new DaemonResult(true, successMessage)
            : new DaemonResult(false, result.Message);
}

internal interface ISystemCommandRunner
{
    Task<SystemCommandResult> RunAsync(string command, string arguments);
}

internal sealed record SystemCommandResult(
    int ExitCode,
    string StandardError,
    string? ExecutionError = null,
    string StandardOutput = "")
{
    public bool Success => ExecutionError is null && ExitCode == 0;

    public string Message => ExecutionError
        ?? (string.IsNullOrWhiteSpace(StandardError)
            ? $"Command exited with code {ExitCode}."
            : StandardError.Trim());
}

internal sealed class ProcessSystemCommandRunner : ISystemCommandRunner
{
    public static ProcessSystemCommandRunner Instance { get; } = new();

    public async Task<SystemCommandResult> RunAsync(string command, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new SystemCommandResult(-1, string.Empty, $"Failed to start command '{command}'.");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            var stdout = await stdoutTask;
            await proc.WaitForExitAsync();

            return new SystemCommandResult(proc.ExitCode, stderr, StandardOutput: stdout);
        }
        catch (Exception ex)
        {
            return new SystemCommandResult(-1, string.Empty, ex.Message);
        }
    }
}
