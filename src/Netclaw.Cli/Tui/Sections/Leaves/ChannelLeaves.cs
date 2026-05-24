// -----------------------------------------------------------------------
// <copyright file="ChannelLeaves.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Slack;
using Netclaw.Cli.Discord;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Channel leaves grouped together — Slack / Discord / Mattermost. They
/// share a common shape (one section per platform with <c>Enabled</c>
/// plus per-channel ACLs and credentials) so co-locating them in one file
/// keeps the call chain readable per the constitution.
/// </summary>
public sealed class ChannelSlackSectionEditor : ISectionEditor
{
    private readonly ISlackProbe _probe;

    public ChannelSlackSectionEditor(ISlackProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    public string SectionId => SectionIds.ChannelSlack;
    public string DisplayName => "Slack";
    public string? Category => "Channels";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } =
    [
        typeof(SlackAuthDoctorCheck),
        typeof(SlackAclDoctorCheck),
    ];

    public SectionStatus GetStatus(SectionEditorContext context) =>
        SectionConfigLookup.IsSectionEnabled(context, "Slack")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;

    public string Summary(SectionEditorContext context)
    {
        if (!SectionConfigLookup.IsSectionEnabled(context, "Slack"))
            return "(disabled)";
        var defaultChannel = SectionConfigLookup.GetStringOrEmpty(context, "Slack.DefaultChannelId");
        var allowedCount = SectionConfigLookup.CountArray(context, "Slack.AllowedChannelIds");
        return string.IsNullOrEmpty(defaultChannel)
            ? $"enabled, {allowedCount} allowed channel(s)"
            : $"default {defaultChannel}, {allowedCount} allowed channel(s)";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SlackStepViewModel(_probe);
    }
}

public sealed class ChannelDiscordSectionEditor : ISectionEditor
{
    private readonly IDiscordProbe _probe;

    public ChannelDiscordSectionEditor(IDiscordProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    public string SectionId => SectionIds.ChannelDiscord;
    public string DisplayName => "Discord";
    public string? Category => "Channels";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [typeof(SecretsJsonDoctorCheck)];

    public SectionStatus GetStatus(SectionEditorContext context) =>
        SectionConfigLookup.IsSectionEnabled(context, "Discord")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;

    public string Summary(SectionEditorContext context)
    {
        if (!SectionConfigLookup.IsSectionEnabled(context, "Discord"))
            return "(disabled)";
        var defaultChannel = SectionConfigLookup.GetStringOrEmpty(context, "Discord.DefaultChannelId");
        return string.IsNullOrEmpty(defaultChannel)
            ? "enabled"
            : $"default {defaultChannel}";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new DiscordStepViewModel(_probe);
    }
}

public sealed class ChannelMattermostSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.ChannelMattermost;
    public string DisplayName => "Mattermost";
    public string? Category => "Channels";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [typeof(SecretsJsonDoctorCheck)];

    public SectionStatus GetStatus(SectionEditorContext context) =>
        SectionConfigLookup.IsSectionEnabled(context, "Mattermost")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;

    public string Summary(SectionEditorContext context)
    {
        if (!SectionConfigLookup.IsSectionEnabled(context, "Mattermost"))
            return "(disabled)";
        var serverUrl = SectionConfigLookup.GetStringOrEmpty(context, "Mattermost.ServerUrl");
        return string.IsNullOrEmpty(serverUrl)
            ? "enabled"
            : $"server {serverUrl}";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new MattermostStepViewModel();
    }
}
