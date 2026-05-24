// -----------------------------------------------------------------------
// <copyright file="IdentitySectionEditor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Leaf editor for agent identity. Synthetic and init-owned per Decision D4:
/// Identity spans <c>netclaw.json</c> (Identity / Workspaces / Notifications)
/// plus generated identity files (<c>SOUL.md</c>, <c>TOOLING.md</c>), so it
/// uses a synthetic <see cref="SectionId"/> and stays out of the
/// <c>netclaw config</c> menu (<see cref="ShowInMenu"/> = <c>false</c>).
/// </summary>
[NoDoctorChecks(
    "Identity is bootstrap-only metadata (agent name, comm style, user name, " +
    "timezone, workspaces directory, optional webhook). None of those values " +
    "have a runtime invariant the doctor checks can verify.")]
public sealed class IdentitySectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.Identity;
    public string DisplayName => "Identity";
    public string? Category => null;
    public bool ShowInMenu => false;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TryGetValue("Identity.AgentName", out var name) && IsNonEmpty(name)
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryGetValue("Identity.AgentName", out var name) || !IsNonEmpty(name))
            return "(not set)";
        return $"agent: {AsString(name)}";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IdentityStepViewModel();
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
