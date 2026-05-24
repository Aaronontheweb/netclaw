// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;

namespace Netclaw.Cli.Tui.ConfigDashboard;

/// <summary>
/// Root information architecture for the post-install <c>netclaw config</c>
/// dashboard. The dashboard is intentionally <b>domain-oriented</b>, not a
/// flat dump of the <see cref="SectionEditorRegistry"/>:
/// the registry remains a leaf catalogue; <see cref="ConfigDashboardViewModel"/>
/// composes those leaves under named domain pages and routes specific
/// entries (Inference Providers, Models, MCP permissions) to dedicated
/// commands per the locked product split.
/// </summary>
public sealed class ConfigDashboardViewModel
{
    public ConfigDashboardViewModel(SectionEditorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
        RootEntries = BuildRootEntries();
    }

    /// <summary>Leaf catalogue resolved from DI.</summary>
    public SectionEditorRegistry Registry { get; }

    /// <summary>
    /// Root dashboard entries in canonical order. Drives the menu rendered
    /// by <c>ConfigDashboardPage</c> and the routing audit tests.
    /// </summary>
    public IReadOnlyList<ConfigDashboardEntry> RootEntries { get; }

    /// <summary>
    /// Root-level affordances available on every page (Quit, Run Full
    /// Doctor). Surfaced separately from <see cref="RootEntries"/> so the
    /// TUI can render them as a sticky footer / key-binding row.
    /// </summary>
    public IReadOnlyList<ConfigDashboardAffordance> Affordances { get; } =
    [
        new("quit", "Quit", ConfigDashboardAffordanceKind.Exit, RouteCommand: null),
        new("doctor", "Run Full Doctor", ConfigDashboardAffordanceKind.RunCommand, RouteCommand: "netclaw doctor"),
    ];

    private List<ConfigDashboardEntry> BuildRootEntries() => new()
    {
        // Routed handoffs — these stay out of the leaf abstraction; the
        // dashboard hands operators off to the existing commands rather
        // than recreating their editors inline.
        ConfigDashboardEntry.Routed(
            id: "inference-providers",
            displayName: "Inference Providers",
            routeCommand: "netclaw provider"),

        ConfigDashboardEntry.Routed(
            id: "models",
            displayName: "Models",
            routeCommand: "netclaw model"),

        // Domain pages — group one or more leaf editors under a named area.
        ConfigDashboardEntry.Domain(
            id: "channels",
            displayName: "Channels",
            description: "Slack, Discord, Mattermost",
            leafIds: ["channel-slack", "channel-discord", "channel-mattermost"]),

        ConfigDashboardEntry.Domain(
            id: "inbound-webhooks",
            displayName: "Inbound Webhooks",
            description: "Receive events from external systems",
            leafIds: ["inbound-webhooks"]),

        ConfigDashboardEntry.Domain(
            id: "skill-sources",
            displayName: "Skill Sources",
            description: "External Skills and Skill Feeds",
            leafIds: ["external-skills", "skill-feeds"]),

        ConfigDashboardEntry.Domain(
            id: "search",
            displayName: "Search",
            description: "Search backend selection and credentials",
            leafIds: ["search"]),

        ConfigDashboardEntry.Domain(
            id: "browser-automation",
            displayName: "Browser Automation",
            description: "MCP-backed browser automation backend",
            leafIds: ["browser-automation"]),

        ConfigDashboardEntry.Domain(
            id: "telemetry-and-alerting",
            displayName: "Telemetry & Alerting",
            description: "Telemetry and Outbound Webhooks",
            leafIds: ["telemetry", "outbound-webhooks"]),

        ConfigDashboardEntry.Domain(
            id: "security-and-access",
            displayName: "Security & Access",
            description: "Security Posture, Enabled Features, Audience Profiles, Exposure Mode",
            leafIds:
            [
                SectionIds.SecurityPosture,
                SectionIds.EnabledFeatures,
                "audience-profiles",
                "exposure-mode",
            ]),
    };
}

/// <summary>
/// One entry on the root dashboard. An entry is either a <b>routed handoff</b>
/// (exec'd via a separate command) or a <b>domain page</b> (renders a
/// sub-menu of registered leaves).
/// </summary>
public sealed record ConfigDashboardEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public ConfigDashboardEntryKind Kind { get; init; }

    /// <summary>For <see cref="ConfigDashboardEntryKind.Routed"/>: the CLI command to exec.</summary>
    public string? RouteCommand { get; init; }

    /// <summary>For <see cref="ConfigDashboardEntryKind.Domain"/>: the section ids the page composes.</summary>
    public IReadOnlyList<string> LeafIds { get; init; } = [];

    public static ConfigDashboardEntry Routed(string id, string displayName, string routeCommand) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Kind = ConfigDashboardEntryKind.Routed,
            RouteCommand = routeCommand,
        };

    public static ConfigDashboardEntry Domain(
        string id, string displayName, string description, IReadOnlyList<string> leafIds) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            Kind = ConfigDashboardEntryKind.Domain,
            LeafIds = leafIds,
        };
}

public enum ConfigDashboardEntryKind
{
    /// <summary>Routes to an existing CLI command (e.g., `netclaw provider`).</summary>
    Routed,

    /// <summary>Renders a sub-page composing one or more leaf editors.</summary>
    Domain,
}

/// <summary>
/// Root-level affordance available on every dashboard page (e.g., Quit,
/// Run Full Doctor). Distinct from <see cref="ConfigDashboardEntry"/>
/// because affordances are not navigated like leaves — they are global
/// actions surfaced in the footer / key bindings.
/// </summary>
public sealed record ConfigDashboardAffordance(
    string Id,
    string DisplayName,
    ConfigDashboardAffordanceKind Kind,
    string? RouteCommand);

public enum ConfigDashboardAffordanceKind
{
    /// <summary>Exit the dashboard.</summary>
    Exit,

    /// <summary>Run an external command and return when it exits.</summary>
    RunCommand,
}
