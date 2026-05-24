// -----------------------------------------------------------------------
// <copyright file="ISectionEditor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui.Wizard;

namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Reusable leaf editor contract for ONE editable surface, runnable from
/// either bootstrap-only <c>netclaw init</c> or post-install
/// <c>netclaw config</c>. The contract describes a leaf — it makes no
/// claim about the top-level navigation shape and SHALL NOT be used to
/// define the dashboard IA. A future config dashboard MAY compose leaves
/// under grouped pages (e.g., <c>Security &amp; Access</c>) and MAY route
/// some entries to existing commands (e.g., <c>netclaw provider</c>) —
/// neither of those decisions belongs in this interface.
/// </summary>
public interface ISectionEditor
{
    /// <summary>
    /// Stable identifier for routing, audit, and exemption lookup. SHALL be
    /// unique across all registered editors; the registry fails fast on
    /// duplicates. Synthetic leaves (e.g., Identity) MAY use a synthetic id
    /// when listed in <see cref="SectionEditorExemptions"/>.
    /// </summary>
    string SectionId { get; }

    /// <summary>User-facing display name. Shown in menus, headers, and dashboard rows.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Optional domain category. The future config dashboard MAY use this
    /// to group leaves under pages; the leaf itself MUST NOT assume any
    /// particular grouping.
    /// </summary>
    string? Category { get; }

    /// <summary>
    /// Whether this leaf should appear in dashboard menus. Init-owned
    /// synthetic leaves (e.g., Identity) SHALL set this to <c>false</c>.
    /// </summary>
    bool ShowInMenu { get; }

    /// <summary>
    /// Doctor checks this leaf is responsible for surfacing. Empty list
    /// requires <see cref="NoDoctorChecksAttribute"/> for the audit to pass.
    /// Items SHALL implement <see cref="IDoctorCheck"/>.
    /// </summary>
    IReadOnlyList<Type> RelevantDoctorChecks { get; }

    /// <summary>
    /// Report the configured state for dashboard listings. SHALL NOT
    /// decrypt secrets — use <see cref="SectionEditorContext.SecretPresent"/>.
    /// </summary>
    SectionStatus GetStatus(SectionEditorContext context);

    /// <summary>
    /// One-line summary of the editor's current state for dashboard rows.
    /// SHALL be safe to display in any audience and SHALL NOT leak secret values.
    /// </summary>
    string Summary(SectionEditorContext context);

    /// <summary>
    /// Build a runnable wizard step viewmodel. The returned viewmodel runs
    /// in either init-owned linear navigation or config-owned single-step
    /// hosting, depending on how the caller drives the orchestrator.
    /// </summary>
    IWizardStepViewModel CreateEditor(WizardContext context);
}
