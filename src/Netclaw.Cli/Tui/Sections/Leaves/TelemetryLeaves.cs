// -----------------------------------------------------------------------
// <copyright file="TelemetryLeaves.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Telemetry (OTLP exporter, sampling) and outbound webhooks (operator
/// alerts on MCP/LLM failure) — paired under "Telemetry &amp; Alerting"
/// per the spec. Delivery-policy tuning is explicitly out of scope for
/// this pass.
/// </summary>
public sealed class TelemetrySectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.Telemetry;
    public string DisplayName => "Telemetry";
    public string? Category => "Telemetry & Alerting";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [typeof(TelemetryDoctorCheck)];

    public SectionStatus GetStatus(SectionEditorContext context) =>
        SectionConfigLookup.SectionExists(context, "Telemetry")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;

    public string Summary(SectionEditorContext context)
    {
        if (!SectionConfigLookup.SectionExists(context, "Telemetry"))
            return "(not configured — exporter disabled)";
        var exporter = SectionConfigLookup.GetStringOrEmpty(context, "Telemetry.Exporter");
        return string.IsNullOrEmpty(exporter)
            ? "configured"
            : $"exporter: {exporter}";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // No dedicated TelemetryStepViewModel exists yet. Returning any
        // other step would silently mutate UNRELATED config sections when
        // single-step hosting calls ContributeConfig — that's worse than
        // failing loud. The dashboard surfaces this state as
        // "editor not yet wired" until the dedicated step lands.
        throw new NotImplementedException(
            "Telemetry editor is not yet wired. Track in netclaw-config-command tasks §7.");
    }
}

public sealed class OutboundWebhooksSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.OutboundWebhooks;
    public string DisplayName => "Outbound Webhooks";
    public string? Category => "Telemetry & Alerting";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } = [typeof(WebhookFormatDoctorCheck)];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        var count = SectionConfigLookup.CountArray(context, "Notifications.Webhooks");
        return count > 0 ? SectionStatus.Configured : SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        var count = SectionConfigLookup.CountArray(context, "Notifications.Webhooks");
        return count == 0 ? "(no outbound alerts)" : $"{count} webhook(s) configured";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // No dedicated OutboundWebhooksStepViewModel exists yet. The init
        // wizard collects the webhook URL inside the Identity step but
        // returning IdentityStepViewModel here would have single-step
        // hosting clobber agent name / timezone / workspaces on save —
        // failing loud is correct until a focused editor lands.
        throw new NotImplementedException(
            "Outbound Webhooks editor is not yet wired. Track in netclaw-config-command tasks §7.");
    }
}
