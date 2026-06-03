// -----------------------------------------------------------------------
// <copyright file="UpdateAvailableDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Checks if a newer version of Netclaw is available on the configured update channel.
/// Uses a short timeout to avoid slowing down doctor runs.
/// </summary>
public sealed class UpdateAvailableDoctorCheck : IDoctorCheck
{
    private const string CheckName = "Update";

    private readonly NetclawPaths _paths;

    public UpdateAvailableDoctorCheck(NetclawPaths paths) => _paths = paths;

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var channel = ResolveChannel();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var httpClient = new HttpClient();
            // FullVersion so a beta build reports its prerelease suffix; the resolved
            // channel keeps doctor consistent with the daemon/CLI (a beta user is told
            // about the next beta, a stable user only about stable releases).
            var result = await UpdateCheckService.CheckForUpdateAsync(
                httpClient, BuildInfo.FullVersion, cts.Token, channel);

            if (result.IsUpdateAvailable)
            {
                return DoctorCheckResult.Warning(
                    CheckName,
                    $"Update available: v{result.CurrentVersion} → v{result.LatestVersion}",
                    "Run `netclaw update` to upgrade.");
            }

            return DoctorCheckResult.Pass(CheckName,
                $"Up to date (v{result.CurrentVersion}).");
        }
        catch
        {
            return DoctorCheckResult.Pass(CheckName,
                $"Could not check for updates (v{BuildInfo.FullVersion}).");
        }
    }

    // Reads Daemon.UpdateChannel from netclaw.json. An invalid value is reported by
    // ConfigSchemaDoctorCheck, so here we fall back to the documented default (stable)
    // rather than failing — the availability check should still run.
    private UpdateChannel ResolveChannel()
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(_paths);
        if (error is not null || root is null)
            return UpdateChannel.Stable;

        var channelStr = (root["Daemon"] as JsonObject)?["UpdateChannel"]?.GetValue<string>();
        try
        {
            return DaemonConfig.ParseUpdateChannel(channelStr);
        }
        catch (InvalidOperationException)
        {
            return UpdateChannel.Stable;
        }
    }
}
