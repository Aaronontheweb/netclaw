// -----------------------------------------------------------------------
// <copyright file="ExposureModeSectionEditor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Exposure Mode leaf under <c>Security &amp; Access</c>. Uses
/// <see cref="ExposureModeStepViewModel"/> for the existing mode-specific
/// dialogs (Reverse Proxy / Tailscale Serve / Tailscale Funnel /
/// Cloudflare Tunnel; Local requires no extra setup). The
/// <see cref="ExposureModeBootstrapGuard"/> companion enforces the
/// auto-pair-on-first-enablement vs. block-on-orphaned-state contract
/// from spec §12.
/// </summary>
public sealed class ExposureModeSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.ExposureMode;
    public string DisplayName => "Exposure Mode";
    public string? Category => "Security & Access";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } =
    [
        typeof(ExposureModeDoctorCheck),
    ];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        // Local mode is implicit when no Daemon section exists.
        return SectionConfigLookup.SectionExists(context, "Daemon")
            ? SectionStatus.Configured
            : SectionStatus.Configured; // Local is also "configured" — there's always an effective mode.
    }

    public string Summary(SectionEditorContext context)
    {
        var mode = SectionConfigLookup.GetStringOrEmpty(context, "Daemon.ExposureMode");
        return string.IsNullOrEmpty(mode) ? "local (default)" : mode;
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ExposureModeStepViewModel();
    }
}

/// <summary>
/// Pre-save guard enforcing the bootstrap/pairing contract from spec §12
/// for the <see cref="ExposureModeSectionEditor"/>. Pure decision logic
/// against on-disk state — no UI; the host invokes this before persisting
/// a non-local mode and reacts to the returned <see cref="ExposureModeBootstrapDecision"/>.
/// </summary>
public static class ExposureModeBootstrapGuard
{
    /// <summary>
    /// Inspect the install's bootstrap / pairing state for the proposed
    /// <paramref name="newMode"/> and return a decision the host SHALL
    /// honor before save.
    /// </summary>
    public static ExposureModeBootstrapDecision Evaluate(
        NetclawPaths paths,
        ExposureMode newMode)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Local mode never triggers pairing or bootstrap concerns.
        if (newMode == ExposureMode.Local)
            return new ExposureModeBootstrapDecision(
                ExposureModeBootstrapAction.Proceed,
                "Local mode requires no pairing.");

        var bootstrapStore = new BootstrapStateStore(paths);
        var hasCompletedBootstrap = bootstrapStore.HasCompletedNonLocalBootstrap();
        var hasPairedDevices = File.Exists(paths.DevicesPath);

        // First-time enablement with NO bootstrap state and NO paired
        // devices — auto-pair the current configuring client per spec
        // scenario "Missing bootstrap state auto-pairs current client".
        if (!hasCompletedBootstrap && !hasPairedDevices)
        {
            return new ExposureModeBootstrapDecision(
                ExposureModeBootstrapAction.AutoPair,
                "No bootstrap or pairing state — the configuring client will be auto-paired before save.");
        }

        // Orphaned / mismatched state — bootstrap marker exists but
        // devices file is missing, or vice versa. Block and point at
        // doctor + docs + #875 per spec scenario "Orphaned bootstrap
        // state blocks save".
        if (hasCompletedBootstrap != hasPairedDevices)
        {
            return new ExposureModeBootstrapDecision(
                ExposureModeBootstrapAction.Block,
                "Bootstrap / pairing state is orphaned or mismatched. " +
                "Run `netclaw doctor`, consult the formal docs, and see issue #875 " +
                "before changing exposure mode. No inline repair is performed.");
        }

        // Both present — normal re-edit of an already-paired install.
        return new ExposureModeBootstrapDecision(
            ExposureModeBootstrapAction.Proceed,
            "Existing pairing and bootstrap state detected; save proceeds.");
    }
}

/// <summary>Decision returned by <see cref="ExposureModeBootstrapGuard"/>.</summary>
public sealed record ExposureModeBootstrapDecision(
    ExposureModeBootstrapAction Action,
    string Message);

public enum ExposureModeBootstrapAction
{
    /// <summary>Save proceeds without bootstrap-related side effects.</summary>
    Proceed,

    /// <summary>Save proceeds after auto-pairing the current configuring client.</summary>
    AutoPair,

    /// <summary>Save is blocked; operator is directed to `netclaw doctor` and docs.</summary>
    Block,
}
