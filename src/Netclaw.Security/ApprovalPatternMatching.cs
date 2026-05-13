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
    /// is either <c>null</c> (the global wildcard) or an ancestor of the
    /// candidate's effective directory with no symlink segments along the
    /// path between the two.
    ///
    /// The candidate's effective directory is
    /// <paramref name="candidateDirectory"/> when non-null (the path argument
    /// extracted from the command), otherwise <paramref name="cwd"/>. Relative
    /// effective directories (<c>./build</c>, <c>../shared</c>) are resolved
    /// against <paramref name="cwd"/> before the under-check.
    ///
    /// The symlink-segment guard prevents a planted symlink under an approved
    /// directory from being used to redirect the candidate to a path outside
    /// that directory: <see cref="PathUtility.ContainsSymlinkSegment"/> walks
    /// each component from the approved root toward the effective directory
    /// and refuses the match if any segment is a reparse point.
    /// </summary>
    public static bool MatchesShellApproval(
        string candidateVerb,
        string? candidateDirectory,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries)
    {
        var effectiveDirectory = ResolveEffectiveDirectory(candidateDirectory, cwd);

        foreach (var entry in approvedEntries)
        {
            if (!string.Equals(entry.Verb, candidateVerb, ApprovalEntryComparison))
                continue;

            // Global wildcard: matches any candidate by definition.
            if (entry.Directory is null)
                return true;

            // Folder-scoped entry requires a concrete effective directory.
            if (string.IsNullOrEmpty(effectiveDirectory))
                continue;

            try
            {
                if (!PathUtility.IsWithinRoot(effectiveDirectory, entry.Directory))
                    continue;

                if (PathUtility.ContainsSymlinkSegment(entry.Directory, effectiveDirectory))
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
    /// Backwards-compatible overload retained for v2.0 callers that pass cwd
    /// only. Equivalent to passing <c>null</c> for the candidate directory.
    /// </summary>
    public static bool MatchesShellApproval(
        string candidateVerb,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries)
        => MatchesShellApproval(candidateVerb, candidateDirectory: null, cwd, approvedEntries);

    /// <summary>
    /// Resolves a candidate's path argument to an absolute path. When the
    /// argument is null, falls back to cwd. When the argument is relative
    /// (<c>./build</c>, <c>../shared</c>, or bare <c>~</c> without expansion),
    /// it is resolved against cwd. Tilde-rooted paths are passed through
    /// unchanged — the storage layer treats <c>~</c> consistently with the
    /// daemon's home expansion via <see cref="PathUtility.ExpandAndNormalize"/>
    /// at match time.
    /// </summary>
    private static string? ResolveEffectiveDirectory(string? candidateDirectory, string? cwd)
    {
        if (string.IsNullOrEmpty(candidateDirectory))
            return cwd;

        if (Path.IsPathRooted(candidateDirectory))
            return candidateDirectory;

        // Tilde-rooted paths look "rooted" to the user but aren't to .NET.
        // Expand against the user's home alongside any cwd-relative segments
        // so we end up with a canonicalized absolute path.
        var expanded = PathUtility.ExpandAndNormalize(candidateDirectory, cwd);
        return expanded ?? candidateDirectory;
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

    /// <summary>
    /// Verbs that produce stdout-only side effects when used without
    /// redirects. A candidate clause whose verb is in this set, has no path
    /// argument, and has no redirect operator is authorized for the current
    /// call but SHALL NOT be persisted — recording every literal echo as a
    /// global wildcard adds noise that doesn't help future matching.
    /// </summary>
    /// <remarks>
    /// Conservative on purpose. <c>eval</c>, <c>command</c>, <c>exec</c>, and
    /// other reflective builtins are NOT here because they execute their
    /// arguments. <c>pwd</c> is a candidate to add but rarely appears in
    /// compound commands so the value is low. Adding entries here is a
    /// security-relevant change reviewed alongside the safe-verb list.
    /// </remarks>
    private static readonly HashSet<string> SideEffectOnlyVerbs = new(StringComparer.Ordinal)
    {
        "echo", "printf", ":", "true", "false"
    };

    /// <summary>
    /// Returns true when this candidate is a pure side-effect clause that
    /// should not be persisted on Always-here/Always-anywhere clicks. The
    /// rule is verb-in-skip-list AND no path argument. Redirect detection
    /// (e.g. <c>echo X &gt; /tmp/log</c>) is implicit: a redirect target
    /// shows up as the candidate's directory via
    /// <see cref="ShellTokenizer.ExtractFirstPathArgument"/>, so a candidate
    /// with a non-null Directory is never considered pure side effect.
    /// </summary>
    public static bool IsPureSideEffect(ApprovalCandidate candidate)
    {
        if (candidate.Directory is not null)
            return false;

        return SideEffectOnlyVerbs.Contains(candidate.Verb);
    }
}
