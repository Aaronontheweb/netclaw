// -----------------------------------------------------------------------
// <copyright file="SectionStatus.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Configured state of a section editor's underlying data, reported by
/// <see cref="ISectionEditor.GetStatus"/>. Surfaces in dashboard listings
/// so operators can see at a glance which leaves are configured.
/// </summary>
public enum SectionStatus
{
    /// <summary>
    /// The section is not currently applicable to the running install
    /// (e.g., posture excludes it). Implies it SHALL NOT appear as a
    /// pending action and SHOULD be hidden or grayed in dashboards.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// The section is applicable but has no stored configuration yet.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The section has stored configuration that the editor recognizes.
    /// </summary>
    Configured,
}
