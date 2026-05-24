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
        return new ProviderStepViewModel(_registry, _probe, _oauthFactory);
    }

    private static bool HasAnyProvider(object providers) =>
        providers switch
        {
            IDictionary<string, object> dict => dict.Count > 0,
            JsonElement je => je.ValueKind == JsonValueKind.Object && je.EnumerateObject().Any(),
            _ => false,
        };
}
