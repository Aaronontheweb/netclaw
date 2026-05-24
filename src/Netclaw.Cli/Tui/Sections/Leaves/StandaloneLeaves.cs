// -----------------------------------------------------------------------
// <copyright file="StandaloneLeaves.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Standalone leaves under the root dashboard — each is its own domain
/// page rather than a member of a grouped area.
/// </summary>
public sealed class SearchSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.Search;
    public string DisplayName => "Search";
    public string? Category => null;
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [typeof(SecretsJsonDoctorCheck)];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        // Default backend (DuckDuckGo) is implicit; an explicit Search
        // section indicates the operator has configured a non-default
        // backend like SearXng.
        return SectionConfigLookup.SectionExists(context, "Search")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        var backend = SectionConfigLookup.GetStringOrEmpty(context, "Search.Backend");
        return string.IsNullOrEmpty(backend) ? "duckduckgo (default)" : backend;
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SearchStepViewModel();
    }
}

public sealed class BrowserAutomationSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.BrowserAutomation;
    public string DisplayName => "Browser Automation";
    public string? Category => null;
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [typeof(McpServersDoctorCheck)];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        // Browser automation surfaces under McpServers as a profile entry.
        return SectionConfigLookup.SectionExists(context, "McpServers")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        return SectionConfigLookup.SectionExists(context, "McpServers")
            ? "configured (MCP-backed)"
            : "(disabled)";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new BrowserAutomationStepViewModel();
    }
}

public sealed class InboundWebhooksSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.InboundWebhooks;
    public string DisplayName => "Inbound Webhooks";
    public string? Category => null;
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [typeof(InboundWebhookRoutesDoctorCheck)];

    public SectionStatus GetStatus(SectionEditorContext context) =>
        SectionConfigLookup.IsSectionEnabled(context, "Webhooks")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;

    public string Summary(SectionEditorContext context)
    {
        if (!SectionConfigLookup.IsSectionEnabled(context, "Webhooks"))
            return "(disabled)";
        return "enabled (routes managed via `netclaw webhooks`)";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // No dedicated inbound-webhooks editor today. Returning the
        // ExposureMode step would silently mutate Daemon.ExposureMode on
        // save, which has nothing to do with inbound webhooks. Operators
        // manage routes via `netclaw webhooks`; enable/disable is wired in
        // the init wizard's ExposureMode step. Fail loud rather than
        // corrupt unrelated config.
        throw new NotImplementedException(
            "Inbound Webhooks editor is not yet wired. " +
            "Manage routes with `netclaw webhooks`; the deployment-wide " +
            "Webhooks.Enabled toggle is set during `netclaw init`. " +
            "Track in netclaw-config-command tasks §5.");
    }
}
