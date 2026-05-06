// -----------------------------------------------------------------------
// <copyright file="ApprovalPatternMatching.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Shared verb-chain prefix matcher used for tool approval grants. An approved
/// pattern matches a candidate exactly or as a verb-chain prefix on a space
/// boundary — so "git push" approves "git push origin main" but never
/// "github-cli". Directory-scoped patterns (trailing <c>/</c>) match any
/// candidate whose path is within the approved directory, using
/// <see cref="PathUtility.IsWithinRoot"/> for boundary-safe containment.
/// </summary>
public static class ApprovalPatternMatching
{
    public static bool MatchesAny(string candidate, IEnumerable<string> approvedPatterns)
    {
        foreach (var approved in approvedPatterns)
        {
            if (string.Equals(candidate, approved, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!approved.Contains(' ', StringComparison.Ordinal))
                continue;

            // Directory-scoped patterns: "verb /dir/" matches "verb /dir/file.txt"
            if (approved.EndsWith('/') && MatchesDirectoryScope(candidate, approved))
                return true;

            // Multi-token patterns prefix-match on a space boundary. Single-token
            // patterns remain exact-only so grants do not silently widen from
            // "cat" to every path-bearing cat invocation.
            if (candidate.Length > approved.Length
                && candidate[approved.Length] == ' '
                && candidate.StartsWith(approved, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool MatchesDirectoryScope(string candidate, string approvedDirPattern)
    {
        var approvedSpaceIdx = approvedDirPattern.IndexOf(' ', StringComparison.Ordinal);
        if (approvedSpaceIdx < 0)
            return false;

        var approvedVerb = approvedDirPattern[..approvedSpaceIdx];
        var approvedDir = approvedDirPattern[(approvedSpaceIdx + 1)..].TrimEnd('/');

        var candidateSpaceIdx = candidate.IndexOf(' ', StringComparison.Ordinal);
        if (candidateSpaceIdx < 0)
            return false;

        var candidateVerb = candidate[..candidateSpaceIdx];
        var candidatePath = candidate[(candidateSpaceIdx + 1)..];

        if (!string.Equals(approvedVerb, candidateVerb, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return PathUtility.IsWithinRoot(candidatePath, approvedDir);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
    }
}
