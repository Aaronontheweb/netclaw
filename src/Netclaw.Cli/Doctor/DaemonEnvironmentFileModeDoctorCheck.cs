// -----------------------------------------------------------------------
// <copyright file="DaemonEnvironmentFileModeDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Reports a <c>daemon.env</c> that group or others can read. The file holds plain-text values that
/// the operator wrote, so install, uninstall, and <c>doctor --fix</c> keep it at mode 0600. An older
/// install or a hand edit can leave it open. <c>doctor --fix</c> sets the mode and changes nothing else.
/// </summary>
public sealed class DaemonEnvironmentFileModeDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "Daemon Env File Mode";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var envPath = paths.DaemonEnvironmentFilePath;
        if (!DaemonPathEnvironmentFile.HasLooseMode(envPath))
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, $"{envPath} is not open to group or others."));

        return Task.FromResult(DoctorCheckResult.Warning(
            CheckName,
            $"{envPath} can be read by other users, and it can hold plain-text values.",
            "Run `netclaw doctor --fix` to set the mode to 0600."));
    }
}
