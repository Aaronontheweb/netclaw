// -----------------------------------------------------------------------
// <copyright file="SectionEditorExemptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Explicit catalogue of synthetic or init-owned leaves whose
/// <see cref="ISectionEditor.SectionId"/> does not map 1:1 to a single
/// top-level config key, AND whose <see cref="ISectionEditor.ShowInMenu"/>
/// is intentionally <c>false</c>. The menu registry audit consults this
/// list so reviewers can see at a glance which leaves are deliberately
/// absent from the config dashboard menu.
/// </summary>
/// <remarks>
/// Identity is the canonical example: it spans <c>netclaw.json</c>
/// (Identity / Workspaces / Notifications) and generated identity files
/// (<c>SOUL.md</c>, <c>TOOLING.md</c>), and remains <c>netclaw init</c>
/// owned per the locked product split.
/// </remarks>
public static class SectionEditorExemptions
{
    /// <summary>
    /// Synthetic, init-owned section ids whose <see cref="ISectionEditor.SectionId"/>
    /// does NOT map 1:1 to a single top-level config key. Section ids in this list:
    /// <list type="bullet">
    ///   <item>SHALL be registered with <see cref="ISectionEditor.ShowInMenu"/> = false.</item>
    ///   <item>MAY use a synthetic identifier that spans multiple config keys or generated files.</item>
    ///   <item>SHALL remain <c>netclaw init</c> owned per the locked split.</item>
    /// </list>
    /// This list intentionally does NOT contain leaves whose only reason to skip
    /// the menu is a routed handoff (e.g., Provider routing to <c>netclaw provider</c>).
    /// Routed handoffs are the next change's concern and are not part of the leaf
    /// abstraction contract.
    /// </summary>
    public static IReadOnlySet<string> SyntheticInitOwnedIds { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // Identity is synthetic: spans config (Identity/Workspaces/Notifications)
            // plus generated SOUL.md and TOOLING.md. Init-owned per the locked split.
            SectionIds.Identity,
        };

    /// <summary>True if the given section id is in the synthetic/init-owned list.</summary>
    public static bool IsSyntheticInitOwned(string sectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        return SyntheticInitOwnedIds.Contains(sectionId);
    }
}

/// <summary>
/// Canonical section ids for built-in leaf editors. Centralized so the
/// audit, registry, and dashboard composer can refer to them by name
/// instead of string literals scattered across the codebase.
/// </summary>
public static class SectionIds
{
    // Bootstrap leaves (init-owned).
    public const string Identity = "identity";
    public const string Provider = "provider";

    // Security & Access.
    public const string SecurityPosture = "security-posture";
    public const string EnabledFeatures = "enabled-features";
    public const string AudienceProfiles = "audience-profiles";
    public const string ExposureMode = "exposure-mode";

    // Channels.
    public const string ChannelSlack = "channel-slack";
    public const string ChannelDiscord = "channel-discord";
    public const string ChannelMattermost = "channel-mattermost";

    // Skill Sources.
    public const string ExternalSkills = "external-skills";
    public const string SkillFeeds = "skill-feeds";

    // Telemetry & Alerting.
    public const string Telemetry = "telemetry";
    public const string OutboundWebhooks = "outbound-webhooks";

    // Standalone.
    public const string Search = "search";
    public const string BrowserAutomation = "browser-automation";
    public const string InboundWebhooks = "inbound-webhooks";
}
