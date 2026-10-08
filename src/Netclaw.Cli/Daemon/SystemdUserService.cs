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
    /// Decides whether the unit owns the daemon of this invocation's home. A running daemon is
    /// the unit's when systemd reports the same main PID the home's pid file records, whatever
    /// NETCLAW_HOME the unit was given and however the home path is spelled. With no daemon
    /// running there is no PID to compare, so only the default home (links resolved) is taken
    /// to be the one the unit serves.
    /// </summary>
    public async Task<SystemdUserServiceOwnership> GetOwnershipAsync(DaemonStatus daemonStatus)
    {
        if (!_enabledOnThisPlatform)
            return SystemdUserServiceOwnership.Unmanaged("systemd user services are Linux-only.");

        if (!File.Exists(_unitFilePath))
            return SystemdUserServiceOwnership.Unmanaged("No netclaw systemd user service is installed.");

        // Another home with nothing running for it: the unit cannot be what starts it.
        if (!daemonStatus.IsRunning && !IsDefaultHome(_homePath))
            return SystemdUserServiceOwnership.Unmanaged(
                $"No daemon is running for {_homePath}, and netclaw.service starts only the default home ({DefaultHomePath}).");

        var active = await _commandRunner.RunAsync("systemctl", $"--user is-active {ServiceName}");

        // The unit's ExecStop= runs `netclaw daemon stop`, so that process is part of a stop job
        // that systemd already started. Asking systemd to stop the unit again would wait on itself.
        if (!active.Success && active.StandardOutput.Trim() == "deactivating")
            return SystemdUserServiceOwnership.Unmanaged("netclaw.service is already stopping.");

        if (daemonStatus.IsRunning)
        {
            if (!active.Success)
                return StateCheckFailed(active) ?? SystemdUserServiceOwnership.Unmanaged(
                    "netclaw.service is not active, so it does not own the running daemon.");

            if (daemonStatus.Pid is not { } daemonPid)
                return SystemdUserServiceOwnership.Unmanaged(
                    "The running daemon's PID is unknown, so netclaw.service cannot be matched to it.");

            var show = await _commandRunner.RunAsync("systemctl", $"--user show {ServiceName} -p MainPID --value");
            return show.Success && int.TryParse(show.StandardOutput.Trim(), out var mainPid) && mainPid != 0 && mainPid == daemonPid
                ? SystemdUserServiceOwnership.Managed($"netclaw.service runs this home's daemon (PID {daemonPid}).")
                : SystemdUserServiceOwnership.Unmanaged(
                    $"netclaw.service does not run this home's daemon (PID {daemonPid}).");
        }

        if (active.Success)
            return SystemdUserServiceOwnership.Managed("netclaw.service is active.");

        var enabled = await _commandRunner.RunAsync("systemctl", $"--user is-enabled --quiet {ServiceName}");
        if (enabled.Success)
            return SystemdUserServiceOwnership.Managed("netclaw.service is enabled.");

        return StateCheckFailed(active, enabled) ?? SystemdUserServiceOwnership.Unmanaged(
            "netclaw.service is installed but neither active nor enabled.");
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
            // Not a readable link: compare the path as spelled.
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
