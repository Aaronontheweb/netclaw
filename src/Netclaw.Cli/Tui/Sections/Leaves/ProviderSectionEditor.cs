// -----------------------------------------------------------------------
// <copyright file="ProviderSectionEditor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Leaf editor for the LLM Inference Provider. <see cref="ShowInMenu"/> is
/// <c>false</c> because the bootstrap flow owns first-run provider selection
/// and post-install edits route to <c>netclaw provider</c> per the locked
/// product split.
/// </summary>
public sealed class ProviderSectionEditor : ISectionEditor
{
    private readonly ProviderDescriptorRegistry _registry;
    private readonly IProviderProbe _probe;
    private readonly DeviceFlowServiceFactory? _oauthFactory;

    public ProviderSectionEditor(
        ProviderDescriptorRegistry registry,
        IProviderProbe probe,
        DeviceFlowServiceFactory? oauthFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(probe);
        _registry = registry;
        _probe = probe;
        _oauthFactory = oauthFactory;
    }

    public string SectionId => SectionIds.Provider;
    public string DisplayName => "Inference Provider";
    public string? Category => null;
    public bool ShowInMenu => false;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } =
    [
        typeof(ContextWindowDoctorCheck),
        typeof(SecretsJsonDoctorCheck),
    ];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryGetValue("Providers", out var providers) || providers is null)
            return SectionStatus.NotConfigured;
        return HasAnyProvider(providers) ? SectionStatus.Configured : SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryGetValue("Providers", out var providers) || providers is null)
            return "(not set)";

        return providers switch
        {
            IDictionary<string, object> dict when dict.Count > 0
                => $"configured: {string.Join(", ", dict.Keys)}",
            JsonElement je when je.ValueKind == JsonValueKind.Object && je.EnumerateObject().Any()
                => $"configured: {string.Join(", ", je.EnumerateObject().Select(p => p.Name))}",
            _ => "(not set)",
        };
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var step = new ProviderStepViewModel(_registry, _probe, _oauthFactory);

        // Init-owned re-entry: prefill provider choice / endpoint / model.
        // Credentials are deliberately NOT prefilled — the view uses
        // ConfigFileHelper.SecretPresent for the "configured — leave blank to
        // keep" hint and the operator re-enters or keeps the existing value.
        var existing = context.ExistingConfig;
        if (existing is null)
            return step;

        if (TryGetProviderEntry(existing, out var providerType, out var endpoint, out var authMethod))
        {
            step.SelectedProviderType = providerType;
            step.EndpointInput = endpoint;
            step.SelectedAuthMethod = authMethod;
        }

        if (TryGetString(existing, "Models", "Main", "ModelId", out var modelId))
            step.SelectedModelId = modelId;

        return step;
    }

    private static bool TryGetProviderEntry(
        IReadOnlyDictionary<string, object> existing,
        out string providerType, out string? endpoint, out AuthMethod authMethod)
    {
        providerType = string.Empty;
        endpoint = null;
        authMethod = AuthMethod.None;

        if (!TryGetSection(existing, "Providers", out var providers) || providers.Count == 0)
            return false;

        // Pick the first registered provider entry. Multi-provider edits
        // route through `netclaw provider` per the locked split; first-run
        // re-entry only honors a single provider.
        var first = providers.First();
        providerType = first.Key;

        if (TryGetEntrySection(first.Value, out var entry))
        {
            if (entry.TryGetValue("Endpoint", out var ep))
                endpoint = AsString(ep);
            if (entry.TryGetValue("AuthMethod", out var am) && AsString(am) is { Length: > 0 } amStr
                && Enum.TryParse<AuthMethod>(amStr, ignoreCase: true, out var parsed))
            {
                authMethod = parsed;
            }
        }
        return true;
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, object> dict,
        string section, string subKey, string field, out string value)
    {
        value = string.Empty;
        if (!TryGetSection(dict, section, out var first))
            return false;
        if (!first.TryGetValue(subKey, out var raw))
            return false;
        if (!TryGetEntrySection(raw, out var inner))
            return false;
        if (!inner.TryGetValue(field, out var leaf))
            return false;
        var s = AsString(leaf);
        if (string.IsNullOrWhiteSpace(s))
            return false;
        value = s;
        return true;
    }

    private static bool TryGetSection(
        IReadOnlyDictionary<string, object> dict, string section,
        out IReadOnlyDictionary<string, object> result)
    {
        if (dict.TryGetValue(section, out var raw) && TryGetEntrySection(raw, out var entry))
        {
            result = entry;
            return true;
        }
        result = new Dictionary<string, object>();
        return false;
    }

    private static bool TryGetEntrySection(
        object? raw, out IReadOnlyDictionary<string, object> result)
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
        result = new Dictionary<string, object>();
        return false;
    }

    private static string AsString(object? v) =>
        v switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? "",
            JsonElement je => je.ToString(),
            _ => "",
        };

    private static bool HasAnyProvider(object providers) =>
        providers switch
        {
            IDictionary<string, object> dict => dict.Count > 0,
            JsonElement je => je.ValueKind == JsonValueKind.Object && je.EnumerateObject().Any(),
            _ => false,
        };
}
