// -----------------------------------------------------------------------
// <copyright file="ApprovalPatternMatching.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Security;

/// <summary>
/// Approval matching helpers that consume the v2 typed
/// <see cref="ApprovalEntry"/> store. Shell approvals use
/// <see cref="MatchesShellApproval"/> which evaluates the candidate's verb
/// chain together with its cwd against each entry's <c>(verb, directory)</c>
/// pair. Other tools use <see cref="MatchesAny"/> for verb-only matching.
/// </summary>
public static class ApprovalPatternMatching
{
    // Case-sensitivity rules live in Netclaw.Configuration so the operator CLI
    // and the daemon gate use exactly the same comparer — see
    // ToolApprovalEntryComparer for the rationale.
    private static StringComparison ApprovalEntryComparison => ToolApprovalEntryComparer.Comparison;

    /// <summary>
    /// Returns true when <paramref name="approvedEntries"/> contains an entry
    /// whose verb equals <paramref name="candidateVerb"/> AND whose directory
    /// is either <c>null</c> (the global wildcard) or an ancestor of
    /// <paramref name="cwd"/> with no symlink segments along the path between
    /// the two.
    ///
    /// The symlink-segment guard prevents a planted symlink under an approved
    /// directory from being used to redirect the cwd to a path outside that
    /// directory: <see cref="PathUtility.ContainsSymlinkSegment"/> walks each
    /// component from the approved root toward the cwd and refuses the match
    /// if any segment is a reparse point.
    /// </summary>
    public static bool MatchesShellApproval(
        string candidateVerb,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries)
    {
        foreach (var entry in approvedEntries)
        {
            if (!string.Equals(entry.Verb, candidateVerb, ApprovalEntryComparison))
                continue;

            // Global wildcard: matches any cwd by definition.
            if (entry.Directory is null)
                return true;

            // Folder-scoped entry requires a concrete cwd to evaluate.
            if (string.IsNullOrEmpty(cwd))
                continue;

            try
            {
                if (!PathUtility.IsWithinRoot(cwd, entry.Directory))
                    continue;

                if (PathUtility.ContainsSymlinkSegment(entry.Directory, cwd))
                    continue;

                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                continue;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="approvedEntries"/> contains an entry
    /// whose verb equals <paramref name="candidate"/>. Used by non-shell
    /// matchers where the directory half of an entry is not meaningful — the
    /// candidate is the tool name and a verb match alone authorizes.
    /// </summary>
    public static bool MatchesAny(string candidate, IEnumerable<ApprovalEntry> approvedEntries)
    {
        foreach (var approved in approvedEntries)
        {
            if (string.Equals(approved.Verb, candidate, ApprovalEntryComparison))
                return true;
        }

        return false;
    }
}
