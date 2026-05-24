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
        var step = new IdentityStepViewModel();

        // Init-owned re-entry: prefill non-secret fields from existing config
        // per the netclaw-onboarding spec. Webhook URL is treated as a
        // semi-secret (URLs may contain tokens); we read it from Notifications
        // only if present and the operator can clear/re-enter explicitly.
        var existing = context.ExistingConfig;
        if (existing is null)
            return step;

        if (TryGetString(existing, "Identity", "AgentName", out var agentName))
            step.AgentName = agentName;
        if (TryGetString(existing, "Identity", "CommunicationStyle", out var commStyle))
            step.CommunicationStyle = commStyle;
        if (TryGetString(existing, "Identity", "UserName", out var userName))
            step.UserName = userName;
        if (TryGetString(existing, "Identity", "UserTimezone", out var timezone))
            step.UserTimezone = timezone;
        if (TryGetString(existing, "Workspaces", "Directory", out var workspaces))
            step.WorkspacesDirectory = workspaces;

        return step;
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, object> dict, string section, string key,
        out string value)
    {
        value = string.Empty;
        if (!TryGetSection(dict, section, out var sectionDict))
            return false;
        if (!sectionDict.TryGetValue(key, out var raw))
            return false;
        return raw switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => Set(out value, s),
            JsonElement je when je.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(je.GetString())
                => Set(out value, je.GetString()!),
            _ => false,
        };

        static bool Set(out string output, string input)
        {
            output = input;
            return true;
        }
    }

    private static bool TryGetSection(
        IReadOnlyDictionary<string, object> dict, string section,
        out IReadOnlyDictionary<string, object> result)
    {
        if (dict.TryGetValue(section, out var raw))
        {
            switch (raw)
            {
                case IReadOnlyDictionary<string, object> ro:
                    result = ro;
                    return true;
                case IDictionary<string, object> rw:
                    result = new Dictionary<string, object>(rw, StringComparer.Ordinal);
                    return true;
                case JsonElement je when je.ValueKind == JsonValueKind.Object:
                    var converted = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var prop in je.EnumerateObject())
                        converted[prop.Name] = prop.Value;
                    result = converted;
                    return true;
            }
        }
        result = new Dictionary<string, object>();
        return false;
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
