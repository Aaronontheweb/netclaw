// -----------------------------------------------------------------------
// <copyright file="EnabledFeaturesSectionEditor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Leaf editor for Enabled Features — deployment-wide runtime enablement of
/// Memory / Search / Skills / Scheduling / SubAgents / Webhooks. Distinct from
/// Security Posture and Audience Profiles per Decision D5: this leaf only
/// controls feature enablement, NOT per-audience policy.
/// </summary>
[NoDoctorChecks(
    "Enabled Features is a coarse-grained toggle list. The individual feature " +
    "subsystems (memory, search, skills, scheduling, subagents, webhooks) each " +
    "carry their own doctor checks; this leaf does not duplicate them.")]
public sealed class EnabledFeaturesSectionEditor : ISectionEditor
{
    private static readonly string[] FeatureKeys =
    [
        "Memory", "Search", "SkillSync", "Scheduling", "SubAgents", "Webhooks",
    ];

    public string SectionId => SectionIds.EnabledFeatures;
    public string DisplayName => "Enabled Features";
    public string? Category => "Security & Access";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The feature toggles all live as Enabled flags inside their own
        // top-level sections. Status is "Configured" once at least one
        // toggle has been explicitly set.
        foreach (var key in FeatureKeys)
        {
            if (context.TryGetValue($"{key}.Enabled", out var v) && IsBool(v))
                return SectionStatus.Configured;
        }
        return SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var enabled = FeatureKeys
            .Where(k => context.TryGetValue($"{k}.Enabled", out var v) && IsTrue(v))
            .ToArray();

        if (enabled.Length == 0)
            return "(none enabled)";

        return $"enabled: {string.Join(", ", enabled)}";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new FeatureSelectionStepViewModel();
    }

    private static bool IsBool(object? v) =>
        v switch
        {
            bool => true,
            JsonElement je when je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False => true,
            _ => false,
        };

    private static bool IsTrue(object? v) =>
        v switch
        {
            bool b => b,
            JsonElement je => je.ValueKind == JsonValueKind.True,
            _ => false,
        };
}
