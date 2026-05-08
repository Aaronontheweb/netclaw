// -----------------------------------------------------------------------
// <copyright file="ApprovalEntry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// One persisted tool-approval grant: a verb chain paired with a directory
/// scope. <see cref="Directory"/> is null for the global wildcard
/// ("approve this verb in any directory"); otherwise it is an absolute path
/// and the entry only matches invocations whose cwd is under that path.
///
/// This record replaces the v1 flat string list in
/// <c>tool-approvals.json</c>. v1 entries (verbs, normalized commands,
/// directory roots, and bash fragments mingled in one list) are not
/// migrated — see <see cref="ToolApprovalStore.Load"/>.
/// </summary>
public sealed record ApprovalEntry
{
    /// <summary>
    /// The verb chain (e.g. <c>git remote</c>, <c>freshdesk</c>). For
    /// <c>shell_execute</c> this is the prefix of non-flag tokens extracted
    /// from a command; for other tools it is the tool name.
    /// </summary>
    [JsonPropertyName("verb")]
    public required string Verb { get; init; }

    /// <summary>
    /// Absolute directory path the grant is scoped to, or <c>null</c> for
    /// the global wildcard. Trailing slashes are normalized away by the
    /// matcher so <c>/path/</c> and <c>/path</c> compare equal.
    /// </summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; init; }
}
