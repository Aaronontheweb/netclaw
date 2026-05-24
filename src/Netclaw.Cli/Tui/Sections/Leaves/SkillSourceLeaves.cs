// -----------------------------------------------------------------------
// <copyright file="SkillSourceLeaves.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// External skill sources (local paths) and skill feeds (remote skill
/// servers) — co-located because both belong under the same "Skill Sources"
/// domain page and share the array-of-source shape.
/// </summary>
public sealed class ExternalSkillsSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.ExternalSkills;
    public string DisplayName => "External Skills";
    public string? Category => "Skill Sources";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        var count = SectionConfigLookup.CountArray(context, "ExternalSkills.Sources");
        return count > 0 ? SectionStatus.Configured : SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        var count = SectionConfigLookup.CountArray(context, "ExternalSkills.Sources");
        return count == 0 ? "(none configured)" : $"{count} source(s)";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ExternalSkillsStepViewModel();
    }
}

public sealed class SkillFeedsSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.SkillFeeds;
    public string DisplayName => "Skill Feeds";
    public string? Category => "Skill Sources";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        var count = SectionConfigLookup.CountArray(context, "SkillFeeds.Feeds");
        return count > 0 ? SectionStatus.Configured : SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        var count = SectionConfigLookup.CountArray(context, "SkillFeeds.Feeds");
        return count == 0 ? "(none configured)" : $"{count} feed(s)";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SkillFeedsStepViewModel();
    }
}
