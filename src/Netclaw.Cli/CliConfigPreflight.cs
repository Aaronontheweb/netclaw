// -----------------------------------------------------------------------
// <copyright file="CliConfigPreflight.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli;

internal static class CliConfigPreflight
{
    public const string MissingConfigMessage = "daemon not configured - please run netclaw init";

    /// <summary>True when the command line asks for help, which never needs the config.</summary>
    public static bool IsHelpRequest(string[] args)
        => (args.Length > 1 && CliArgsParser.IsHelpToken(args[1]))
           || CliArgsParser.HasTrailingHelpToken(args, 1, includeBareHelp: false);

    /// <summary>
    /// Adds netclaw.json and secrets.json the way the daemon reads them (the standard JSON
    /// provider, so comments and trailing commas are accepted). A file that cannot be loaded is
    /// left out instead of throwing; <see cref="TryDescribeUnreadableConfig"/> reports it.
    /// </summary>
    public static IConfigurationBuilder AddConfigFiles(IConfigurationBuilder builder, NetclawPaths paths)
    {
        foreach (var file in ConfigFiles(paths))
        {
            if (!TryDescribeUnreadableFile(file, out _))
                builder.AddJsonFile(file, optional: true, reloadOnChange: false);
        }

        return builder;
    }

    /// <summary>
    /// Loads each config file with the provider the daemon uses and, for the first one that
    /// fails, returns one line naming the file and the reason (with line and position for a
    /// parse error).
    /// </summary>
    public static bool TryDescribeUnreadableConfig(NetclawPaths paths, out string message)
    {
        foreach (var file in ConfigFiles(paths))
        {
            if (TryDescribeUnreadableFile(file, out message))
                return true;
        }

        message = string.Empty;
        return false;
    }

    private static bool TryDescribeUnreadableFile(string file, out string message)
    {
        try
        {
            new ConfigurationBuilder().AddJsonFile(file, optional: true, reloadOnChange: false).Build();
            message = string.Empty;
            return false;
        }
        catch (Exception ex)
        {
            // The provider wraps parse errors in InvalidDataException/FormatException;
            // the innermost message is the one that says what is wrong.
            while (ex is InvalidDataException or FormatException && ex.InnerException is { } inner)
                ex = inner;

            message = $"Couldn't read {Path.GetFileName(file)}: {ex.Message.ReplaceLineEndings(" ")}";
            return true;
        }
    }

    private static string[] ConfigFiles(NetclawPaths paths) => [paths.NetclawConfigPath, paths.SecretsPath];

    public static bool TryWriteMissingChatConfig(
        NetclawPaths paths,
        string mode,
        bool chatJsonOutput,
        TextWriter writer,
        out int exitCode)
    {
        if (mode is not ("chat" or "headless"))
        {
            exitCode = 0;
            return false;
        }

        return TryWriteMissingConfig(
            paths,
            jsonOutput: mode == "headless" && chatJsonOutput,
            writer,
            out exitCode);
    }

    public static bool TryWriteMissingConfig(
        NetclawPaths paths,
        bool jsonOutput,
        TextWriter writer,
        out int exitCode)
    {
        if (File.Exists(paths.NetclawConfigPath))
        {
            exitCode = 0;
            return false;
        }

        // A missing local daemon config only means "not initialized" when THIS
        // machine hosts the daemon. When the caller explicitly targets a remote
        // daemon (NETCLAW_DAEMON_ENDPOINT) or this install is a paired client
        // (client.json endpoint), the daemon's configuration lives on the
        // daemon host — blocking here regressed paired/remote clients and the
        // containerized eval harness after #1540. These are exactly the
        // endpoint sources DaemonApi.ResolveEndpoint prefers over the local
        // daemon config.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT"))
            || ClientConfigFile.ReadEndpoint(paths) is not null)
        {
            exitCode = 0;
            return false;
        }

        if (jsonOutput)
        {
            var node = new JsonObject
            {
                ["overall"] = "not-configured",
                ["message"] = MissingConfigMessage,
            };
            writer.WriteLine(node.ToJsonString(JsonDefaults.Indented));
        }
        else
        {
            writer.WriteLine(MissingConfigMessage);
        }

        exitCode = 1;
        return true;
    }
}
