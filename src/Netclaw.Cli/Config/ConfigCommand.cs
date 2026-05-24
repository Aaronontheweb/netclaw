// -----------------------------------------------------------------------
// <copyright file="ConfigCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Config;

/// <summary>
/// Top-level <c>netclaw config</c> command. Hosts the domain-oriented
/// post-install settings dashboard (see <c>ConfigDashboardPage</c>) and
/// enforces the "missing install refuses with a plain non-zero message"
/// contract from the netclaw-config-command spec.
/// </summary>
internal static class ConfigCommand
{
    /// <summary>
    /// Pre-flight check: <c>netclaw config</c> requires an existing install.
    /// Returns <c>true</c> when the install is detected; otherwise emits a
    /// plain non-zero message pointing at <c>netclaw init</c> and returns
    /// <c>false</c>. Callers SHALL set <c>Environment.ExitCode</c> to a
    /// non-zero value when this returns <c>false</c> and SHALL NOT render
    /// any TUI.
    /// </summary>
    internal static bool PreflightOrRefuse(NetclawPaths paths, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(output);

        if (File.Exists(paths.NetclawConfigPath))
            return true;

        output.WriteLine("[FAIL] netclaw config: no installation found at " + paths.NetclawConfigPath);
        output.WriteLine("hint: run `netclaw init` to bootstrap a new installation before using `netclaw config`.");
        return false;
    }

    /// <summary>
    /// Render help text for <c>netclaw config</c>. Mirrors the style of
    /// other top-level help writers in <c>Program.cs</c>.
    /// </summary>
    internal static void WriteHelp(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine("Usage: netclaw config");
        output.WriteLine();
        output.WriteLine("Launch the post-install settings dashboard. Use this for ongoing");
        output.WriteLine("configuration after `netclaw init`.");
        output.WriteLine();
        output.WriteLine("Some areas route to other commands rather than recreating their");
        output.WriteLine("editors inside the dashboard:");
        output.WriteLine();
        output.WriteLine("  Inference Providers  -> netclaw provider");
        output.WriteLine("  Models               -> netclaw model");
        output.WriteLine("  MCP permissions      -> netclaw mcp permissions");
        output.WriteLine();
        output.WriteLine("Refusal: if no installation is present, `netclaw config` exits");
        output.WriteLine("non-zero and prints a hint to run `netclaw init`. No partial TUI");
        output.WriteLine("is rendered without an existing config.");
    }
}
