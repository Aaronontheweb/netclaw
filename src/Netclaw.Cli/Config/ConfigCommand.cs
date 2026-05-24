// -----------------------------------------------------------------------
// <copyright file="ConfigCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Config;

/// <summary>
/// Top-level <c>netclaw config</c> command surface. The Termina-hosted
/// dashboard lives in <c>Tui/ConfigDashboard/ConfigDashboardPage</c>;
/// missing-install refusal is handled inline in <c>Program.cs</c> so the
/// stderr message and exit code stay on the actual production path.
/// </summary>
internal static class ConfigCommand
{
    /// <summary>Render help text for <c>netclaw config</c>.</summary>
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
        output.WriteLine("Refusal: if no installation is present, `netclaw config` writes");
        output.WriteLine("`No configuration found. Run `netclaw init` first.` to stderr and");
        output.WriteLine("exits non-zero. No partial TUI is rendered without an existing config.");
    }
}
