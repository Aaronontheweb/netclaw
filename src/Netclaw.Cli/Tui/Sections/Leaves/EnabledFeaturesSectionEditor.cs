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
        var step = new FeatureSelectionStepViewModel();

        // Init-owned / config-owned re-entry: prefill the toggle row from
        // existing config so re-opening this leaf doesn't visually reset
        // every feature to its posture default. Same contract as the
        // other init-owned leaves (Provider / Identity / SecurityPosture).
        var existing = context.ExistingConfig;
        if (existing is null)
            return step;

        for (var i = 0; i < FeatureKeys.Length; i++)
        {
            if (TryReadEnabled(existing, FeatureKeys[i], out var enabled) && enabled
                && !step.IsFeatureEnabled(i))
            {
                step.ToggleFeature(i);
            }
            else if (TryReadEnabled(existing, FeatureKeys[i], out var disabled) && !disabled
                && step.IsFeatureEnabled(i))
            {
                step.ToggleFeature(i);
            }
        }

        return step;
    }

    private static bool TryReadEnabled(
        IReadOnlyDictionary<string, object> existing, string sectionKey, out bool value)
    {
        value = false;
        if (!existing.TryGetValue(sectionKey, out var raw) || raw is null)
            return false;

        object? enabledRaw = raw switch
        {
            IReadOnlyDictionary<string, object> ro when ro.TryGetValue("Enabled", out var v) => v,
            IDictionary<string, object> rw when rw.TryGetValue("Enabled", out var v) => v,
            JsonElement je when je.ValueKind == JsonValueKind.Object
                && je.TryGetProperty("Enabled", out var prop) => prop,
            _ => null,
        };

        switch (enabledRaw)
        {
            case bool b:
                value = b;
                return true;
            case JsonElement je when je.ValueKind == JsonValueKind.True:
                value = true;
                return true;
            case JsonElement je when je.ValueKind == JsonValueKind.False:
                value = false;
                return true;
            default:
                return false;
        }
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
