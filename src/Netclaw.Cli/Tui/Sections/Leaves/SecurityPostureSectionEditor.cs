// -----------------------------------------------------------------------
// <copyright file="SecurityPostureSectionEditor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Leaf editor for Security Posture (<c>Personal</c> / <c>Team</c> /
/// <c>Public</c>). Reusable under the future <c>netclaw config</c>
/// <c>Security &amp; Access</c> domain page; explicitly distinct from
/// <c>Enabled Features</c> and <c>Audience Profiles</c> per Decision D5.
/// </summary>
public sealed class SecurityPostureSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.SecurityPosture;
    public string DisplayName => "Security Posture";
    public string? Category => "Security & Access";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } =
    [
        typeof(SecurityPolicyDoctorCheck),
        typeof(ToolAudienceProfilesDoctorCheck),
    ];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TryGetValue("Security.DeploymentPosture", out var v) && IsNonEmpty(v)
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryGetValue("Security.DeploymentPosture", out var v) || !IsNonEmpty(v))
            return "(not set — defaults to Personal)";
        return $"posture: {AsString(v)}";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var step = new SecurityPostureStepViewModel();

        // Init-owned re-entry: prefill the selected posture from existing
        // config so the operator sees their current posture preselected per
        // the section-editor-abstraction spec scenario "Existing non-secret
        // values prefill".
        if (context.ExistingConfig is not null
            && TryGetPostureString(context.ExistingConfig, out var postureStr)
            && Enum.TryParse<Netclaw.Configuration.DeploymentPosture>(
                postureStr, ignoreCase: true, out var posture))
        {
            step.SelectedPosture = posture;
        }

        return step;
    }

    private static bool TryGetPostureString(
        IReadOnlyDictionary<string, object> dict, out string value)
    {
        value = string.Empty;
        if (!dict.TryGetValue("Security", out var sec)) return false;

        object? rawPosture = sec switch
        {
            IReadOnlyDictionary<string, object> ro when ro.TryGetValue("DeploymentPosture", out var v) => v,
            IDictionary<string, object> rw when rw.TryGetValue("DeploymentPosture", out var v) => v,
            JsonElement je when je.ValueKind == JsonValueKind.Object
                && je.TryGetProperty("DeploymentPosture", out var prop) => prop,
            _ => null,
        };

        var s = rawPosture switch
        {
            string str => str,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(s)) return false;
        value = s!;
        return true;
    }

    private static bool IsNonEmpty(object? v) =>
        v switch
        {
            string s => !string.IsNullOrWhiteSpace(s),
            JsonElement je => je.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(je.GetString()),
            _ => false,
        };

    private static string AsString(object? v) =>
        v switch
        {
            string s => s,
            JsonElement je => je.GetString() ?? "",
            _ => "",
        };
}
