// -----------------------------------------------------------------------
// <copyright file="DaemonConfigurationSources.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>The configuration sources the daemon and the CLI both read, and how a failure to read them is worded.</summary>
public static class DaemonConfigurationSources
{
    /// <summary>
    /// Adds the daemon configuration sources. A later source overrides an earlier source:
    /// netclaw.json, then secrets.json, then oauth-state.json, then <c>NETCLAW_*</c> environment
    /// variables. oauth-state.json holds only token expiry, so it overrides the retired
    /// <c>OAuthTokenExpiry</c> key in netclaw.json.
    /// </summary>
    public static IConfigurationBuilder AddNetclawDaemonSources(this IConfigurationBuilder builder, NetclawPaths paths)
        => builder
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
            .AddJsonFile(paths.OAuthStatePath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NETCLAW_");

    /// <summary>
    /// One line for a configuration file that the JSON provider could not load:
    /// <c>Couldn't read &lt;file&gt;: &lt;reason&gt;</c>. The provider wraps the cause in
    /// <see cref="InvalidDataException"/> and <see cref="FormatException"/>; the innermost message
    /// says what is wrong, with line and position for a parse error.
    /// </summary>
    public static string DescribeLoadFailure(Exception ex)
    {
        var file = "netclaw.json, secrets.json, or oauth-state.json";
        for (var e = ex; e is not null; e = e.InnerException)
        {
            const string prefix = "Failed to load configuration from file '";
            if (e is InvalidDataException && e.Message.StartsWith(prefix, StringComparison.Ordinal)
                && e.Message.IndexOf('\'', prefix.Length) is var end and > 0)
            {
                file = Path.GetFileName(e.Message[prefix.Length..end]);
                break;
            }
        }

        return DescribeLoadFailure(file, ex);
    }

    public static string DescribeLoadFailure(string file, Exception ex)
    {
        while (ex is InvalidDataException or FormatException && ex.InnerException is { } inner)
            ex = inner;

        var reason = ex.Message;
        var lineEnd = reason.IndexOfAny(['\r', '\n']);
        return $"Couldn't read {file}: {(lineEnd < 0 ? reason : reason[..lineEnd])}";
    }
}
