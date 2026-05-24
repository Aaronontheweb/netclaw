// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.ConfigDashboard;

/// <summary>
/// Root information architecture for the post-install <c>netclaw config</c>
/// dashboard. Domain-oriented (per spec §3 "the root SHALL be navigation-first
/// and SHALL NOT be a flat list of every registered leaf editor"), with routed
/// handoffs for <c>Inference Providers</c> / <c>Models</c> dispatched via
/// Termina in-process navigation.
/// </summary>
public sealed class ConfigDashboardViewModel : ReactiveViewModel
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
    /// by <see cref="ConfigDashboardPage"/> and the routing audit tests.
    /// </summary>
    public IReadOnlyList<ConfigDashboardEntry> RootEntries { get; }

    /// <summary>
    /// Root-level affordances available on every page (Quit, Run Full
    /// Doctor). Surfaced separately from <see cref="RootEntries"/> so the
    /// page can render them as a sticky footer / key-binding row.
    /// </summary>
    public IReadOnlyList<ConfigDashboardAffordance> Affordances { get; } =
    [
        new("quit", "Quit", ConfigDashboardAffordanceKind.Exit, RouteCommand: null),
        new("doctor", "Run Full Doctor", ConfigDashboardAffordanceKind.RunCommand, RouteCommand: "netclaw doctor"),
    ];

    /// <summary>
    /// Emits the last action taken (route navigation, exit, sub-menu enter)
    /// for tests + integration probes. Tests can subscribe before invoking
    /// <see cref="ActivateEntry"/> / <see cref="ActivateAffordance"/> to
    /// assert the dispatch decision without needing a Termina host.
    /// </summary>
    public Observable<ConfigDashboardAction> Actions => _actions;
    private readonly Subject<ConfigDashboardAction> _actions = new();

    /// <summary>
    /// Currently selected Domain entry's sub-menu, or null when the operator
    /// is on the root menu. Drives the <see cref="ConfigDashboardPage"/>
    /// sub-menu render and lets <see cref="GoBackToRoot"/> unwind correctly.
    /// </summary>
    public ReactiveProperty<ConfigDashboardEntry?> ActiveDomain { get; } = new(null);

    /// <summary>
    /// Operator activated an entry on the root menu. Routed entries
    /// navigate via Termina's in-process router; Domain entries open
    /// their sub-menu by setting <see cref="ActiveDomain"/>.
    /// </summary>
    public void ActivateEntry(ConfigDashboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        switch (entry.Kind)
        {
            case ConfigDashboardEntryKind.Routed when !string.IsNullOrWhiteSpace(entry.RouteCommand):
                var route = MapRouteCommandToTerminaPath(entry.RouteCommand);
                Navigate(route);
                _actions.OnNext(new ConfigDashboardAction(
                    ConfigDashboardActionKind.NavigatedToRoute, entry, route));
                return;

            case ConfigDashboardEntryKind.Domain:
                ActiveDomain.Value = entry;
                _actions.OnNext(new ConfigDashboardAction(
                    ConfigDashboardActionKind.OpenedDomain, entry, entry.Id));
                return;

            default:
                throw new InvalidOperationException(
                    $"Unknown entry kind {entry.Kind} for id '{entry.Id}'.");
        }
    }

    /// <summary>
    /// Operator activated a root-level affordance (Quit, Run Full Doctor).
    /// Exit hands off to <see cref="ReactiveViewModel.RequestShutdown"/>;
    /// Run Full Doctor publishes an action so the host can render the
    /// operator hint (doctor is its own CLI surface and is not a Termina
    /// page in this build).
    /// </summary>
    public void ActivateAffordance(ConfigDashboardAffordance affordance)
    {
        ArgumentNullException.ThrowIfNull(affordance);
        switch (affordance.Kind)
        {
            case ConfigDashboardAffordanceKind.Exit:
                _actions.OnNext(new ConfigDashboardAction(
                    ConfigDashboardActionKind.Exited, Entry: null, Detail: null));
                RequestShutdown();
                return;

            case ConfigDashboardAffordanceKind.RunCommand when !string.IsNullOrWhiteSpace(affordance.RouteCommand):
                _actions.OnNext(new ConfigDashboardAction(
                    ConfigDashboardActionKind.RanExternalCommand, Entry: null, Detail: affordance.RouteCommand));
                return;

            default:
                throw new InvalidOperationException(
                    $"Unknown affordance kind {affordance.Kind} for id '{affordance.Id}'.");
        }
    }

    /// <summary>Return from a Domain sub-menu to the root menu.</summary>
    public void GoBackToRoot()
    {
        if (ActiveDomain.Value is null) return;
        var prior = ActiveDomain.Value;
        ActiveDomain.Value = null;
        _actions.OnNext(new ConfigDashboardAction(
            ConfigDashboardActionKind.ReturnedToRoot, Entry: prior, Detail: null));
    }

    public override void Dispose()
    {
        _actions.Dispose();
        ActiveDomain.Dispose();
        base.Dispose();
    }

    private List<ConfigDashboardEntry> BuildRootEntries() => new()
    {
        ConfigDashboardEntry.Routed(
            id: "inference-providers",
            displayName: "Inference Providers",
            routeCommand: "netclaw provider"),

        ConfigDashboardEntry.Routed(
            id: "models",
            displayName: "Models",
            routeCommand: "netclaw model"),

        ConfigDashboardEntry.Domain(
            id: "channels",
            displayName: "Channels",
            description: "Slack, Discord, Mattermost",
            leafIds: [SectionIds.ChannelSlack, SectionIds.ChannelDiscord, SectionIds.ChannelMattermost]),

        ConfigDashboardEntry.Domain(
            id: "inbound-webhooks",
            displayName: "Inbound Webhooks",
            description: "Receive events from external systems",
            leafIds: [SectionIds.InboundWebhooks]),

        ConfigDashboardEntry.Domain(
            id: "skill-sources",
            displayName: "Skill Sources",
            description: "External Skills and Skill Feeds",
            leafIds: [SectionIds.ExternalSkills, SectionIds.SkillFeeds]),

        ConfigDashboardEntry.Domain(
            id: "search",
            displayName: "Search",
            description: "Search backend selection and credentials",
            leafIds: [SectionIds.Search]),

        ConfigDashboardEntry.Domain(
            id: "browser-automation",
            displayName: "Browser Automation",
            description: "MCP-backed browser automation backend",
            leafIds: [SectionIds.BrowserAutomation]),

        ConfigDashboardEntry.Domain(
            id: "telemetry-and-alerting",
            displayName: "Telemetry & Alerting",
            description: "Telemetry and Outbound Webhooks",
            leafIds: [SectionIds.Telemetry, SectionIds.OutboundWebhooks]),

        ConfigDashboardEntry.Domain(
            id: "security-and-access",
            displayName: "Security & Access",
            description: "Security Posture, Enabled Features, Audience Profiles, Exposure Mode",
            leafIds:
            [
                SectionIds.SecurityPosture,
                SectionIds.EnabledFeatures,
                SectionIds.AudienceProfiles,
                SectionIds.ExposureMode,
            ]),
    };

    /// <summary>
    /// Translate spec-text route commands (e.g., <c>"netclaw provider"</c>)
    /// to in-process Termina route paths (e.g., <c>"/provider"</c>). MCP
    /// permissions land at <c>/mcp-tools</c> per the existing route
    /// registration in <c>Program.cs</c>.
    /// </summary>
    internal static string MapRouteCommandToTerminaPath(string routeCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeCommand);
        return routeCommand switch
        {
            "netclaw provider" => "/provider",
            "netclaw model" => "/model",
            "netclaw mcp permissions" => "/mcp-tools",
            _ => throw new InvalidOperationException(
                $"No Termina route is registered for routed command '{routeCommand}'. " +
                "Update Program.cs `AddTermina(...)` and this mapping in lockstep."),
        };
    }
}

/// <summary>
/// One entry on the root dashboard. An entry is either a <b>routed handoff</b>
/// (Termina <c>Navigate("/&lt;path&gt;")</c>) or a <b>domain page</b> (renders
/// a sub-menu of registered leaves).
/// </summary>
public sealed record ConfigDashboardEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public ConfigDashboardEntryKind Kind { get; init; }

    /// <summary>For <see cref="ConfigDashboardEntryKind.Routed"/>: spec-text CLI command (e.g., <c>"netclaw provider"</c>).</summary>
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
    /// <summary>Routes via Termina in-process Navigate to an existing TUI page.</summary>
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
    /// <summary>Exit the dashboard (Termina Shutdown).</summary>
    Exit,

    /// <summary>Surface a non-Termina CLI command for the operator to run after exit.</summary>
    RunCommand,
}

/// <summary>Record of an action dispatched by <see cref="ConfigDashboardViewModel"/>.</summary>
public sealed record ConfigDashboardAction(
    ConfigDashboardActionKind Kind,
    ConfigDashboardEntry? Entry,
    string? Detail);

public enum ConfigDashboardActionKind
{
    NavigatedToRoute,
    OpenedDomain,
    ReturnedToRoot,
    Exited,
    RanExternalCommand,
}
