// -----------------------------------------------------------------------
// <copyright file="SystemdUnitPathDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Validates that the systemd <c>--user</c> unit installed by
/// <c>netclaw daemon install</c> bakes a PATH that resolves the daemon's
/// install directory. Without this, <c>ShellTool</c> and
/// <c>BackgroundJobExecutionActor</c> spawn <c>bash -c</c> with the
/// sanitized systemd default PATH and cannot find <c>netclaw</c>,
/// <c>~/.local/bin</c> tools, or anything else outside the system path.
/// </summary>
/// <remarks>
/// This is a Linux-only diagnostic. On non-Linux platforms — and on Linux
/// boxes where the user runs <c>netclaw daemon start</c> directly instead
/// of installing the service — this check passes silently because the
/// daemon inherits the operator's interactive shell PATH and the failure
/// mode does not apply.
/// </remarks>
public sealed class SystemdUnitPathDoctorCheck : IDoctorCheck
{
    private const string CheckName = "Systemd Unit PATH";
    private const string ExecStartPrefix = "ExecStart=";
    private const string PathDirectivePrefix = "Environment=PATH=";

    private readonly string _unitFilePath;
    private readonly bool _enabledOnThisPlatform;

    public SystemdUnitPathDoctorCheck()
        : this(DaemonManager.SystemdUserUnitFilePath, OperatingSystem.IsLinux())
    {
    }

    /// <summary>
    /// Test seam: explicit unit path and platform gate so tests can exercise
    /// the parser on any host without needing a real systemd installation.
    /// </summary>
    internal SystemdUnitPathDoctorCheck(string unitFilePath, bool enabledOnThisPlatform)
    {
        _unitFilePath = unitFilePath;
        _enabledOnThisPlatform = enabledOnThisPlatform;
    }

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabledOnThisPlatform)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Not applicable on this platform."));

        var unitPath = _unitFilePath;

        if (!File.Exists(unitPath))
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                CheckName,
                "No systemd user service installed (skipping)."));
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(unitPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"Could not read {unitPath}: {ex.Message}",
                "Check file permissions."));
        }

        var execStart = FindDirective(lines, ExecStartPrefix);
        if (execStart is null)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"{unitPath} is missing ExecStart=. Unit file may be malformed.",
                "Reinstall: `netclaw daemon uninstall && netclaw daemon install`."));
        }

        var installDir = Path.GetDirectoryName(ExtractFirstToken(execStart));
        if (string.IsNullOrEmpty(installDir))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"Could not determine install directory from ExecStart in {unitPath}.",
                "Reinstall: `netclaw daemon uninstall && netclaw daemon install`."));
        }

        var pathDirective = FindDirective(lines, PathDirectivePrefix);
        if (pathDirective is null)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"Systemd unit at {unitPath} does not set PATH. The daemon's shell tool will fail to resolve `netclaw`, " +
                "`~/.local/bin` tools, and anything outside the systemd default PATH.",
                "Reinstall to refresh the unit file: `netclaw daemon uninstall && netclaw daemon install`, " +
                "then `systemctl --user restart netclaw`."));
        }

        var pathValue = pathDirective[PathDirectivePrefix.Length..];
        var entries = pathValue.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasInstallDir = entries.Any(e => string.Equals(e, installDir, StringComparison.Ordinal));

        if (!hasInstallDir)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"Systemd unit PATH at {unitPath} does not include the daemon's install directory ({installDir}). " +
                "Shell tool invocations may fail to resolve `netclaw`.",
                "Reinstall: `netclaw daemon uninstall && netclaw daemon install`."));
        }

        return Task.FromResult(DoctorCheckResult.Pass(
            CheckName,
            $"Systemd unit PATH includes {installDir} ({entries.Length} entries)."));
    }

    /// <summary>
    /// Returns the first line whose trimmed start matches <paramref name="prefix"/>,
    /// stripped of leading whitespace. systemd unit files allow whitespace before
    /// directives; we accept it. Returns <c>null</c> if no match exists.
    /// </summary>
    private static string? FindDirective(string[] lines, string prefix)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line;
        }

        return null;
    }

    /// <summary>
    /// Extracts the first whitespace-delimited token from a directive value
    /// (e.g., <c>ExecStart=/path/to/netclawd --flag</c> → <c>/path/to/netclawd</c>).
    /// </summary>
    private static string ExtractFirstToken(string directive)
    {
        var equalsIndex = directive.IndexOf('=');
        if (equalsIndex < 0 || equalsIndex == directive.Length - 1)
            return string.Empty;

        var value = directive[(equalsIndex + 1)..].TrimStart();
        var spaceIndex = value.IndexOf(' ');
        return spaceIndex < 0 ? value : value[..spaceIndex];
    }
}
