// -----------------------------------------------------------------------
// <copyright file="TrustState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using ShellSyntaxTree;

namespace Netclaw.Security;

/// <summary>
/// Composed trust state for one audience evaluating one tool call. Snapshots
/// every input the gate evaluator needs: the audience-baseline zones from
/// <c>netclaw.json</c>, the persisted zones and verb patterns from the
/// <c>AudienceTrustStore</c>, the in-memory session-scope zones and verb
/// patterns from <c>LlmSessionActor</c>, the immutable session directory,
/// and the shipped read-only verb list.
/// </summary>
/// <remarks>
/// Construct once per gate evaluation. Normalization (tilde expansion,
/// trailing-slash strip) happens at construction time so the matcher does
/// no per-call resolution beyond the path comparisons themselves.
///
/// Zone matching is path-prefix recursive with directory-boundary safety:
/// zone <c>~/repos</c> matches <c>~/repos/foo/bar</c> but not
/// <c>~/repossecret</c>. Verb-pattern matching is verb-chain prefix +
/// arg-glob suffix per the locked design (<c>git push *</c> matches
/// <c>git push origin main</c> but not <c>git pull origin main</c>).
/// </remarks>
public sealed class TrustState
{
    private readonly IReadOnlyList<string> _normalizedZones;
    private readonly IReadOnlyList<string> _allVerbPatterns;
    private readonly SafeVerbList _readOnlyVerbs;

    public TrustState(
        IEnumerable<string> baselineZones,
        IEnumerable<string> persistedZones,
        IEnumerable<string> sessionZones,
        string sessionDirectory,
        IEnumerable<string> persistedVerbPatterns,
        IEnumerable<string> sessionVerbPatterns,
        SafeVerbList readOnlyVerbs,
        string? homeDirectory = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Compose all four zone sources, normalize each, dedupe. Order
        // doesn't matter for matching — we OR across all zones.
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in baselineZones.Concat(persistedZones).Concat(sessionZones))
        {
            var normalized = NormalizeZone(zone, home);
            if (!string.IsNullOrEmpty(normalized))
                zones.Add(normalized);
        }

        // session_dir is always trusted, even when it's empty/whitespace
        // we still avoid adding a degenerate empty zone.
        var normalizedSessionDir = NormalizeZone(sessionDirectory, home);
        if (!string.IsNullOrEmpty(normalizedSessionDir))
            zones.Add(normalizedSessionDir);

        _normalizedZones = zones.ToArray();

        // Verb patterns: persisted + session, deduped. No normalization
        // beyond trim — pattern format is locked at AudienceTrustStore
        // write time (verb-chain prefix + arg-glob suffix).
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in persistedVerbPatterns.Concat(sessionVerbPatterns))
        {
            var trimmed = pattern?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                patterns.Add(trimmed);
        }

        _allVerbPatterns = patterns.ToArray();
        _readOnlyVerbs = readOnlyVerbs ?? throw new ArgumentNullException(nameof(readOnlyVerbs));
    }

    /// <summary>
    /// True when the resolved absolute path lies inside any trusted zone in
    /// the composed state. Path-prefix recursive matching with
    /// directory-boundary safety.
    /// </summary>
    public bool IsPathInTrustedZone(string resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return false;

        foreach (var zone in _normalizedZones)
        {
            if (IsSamePathOrChild(resolvedPath, zone))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the verb chain (case-rules per platform) is on the shipped
    /// read-only safe-verbs list. The list is immutable at runtime and
    /// loaded from the embedded resource — see
    /// <see cref="SafeVerbLoader"/>.
    /// </summary>
    public bool IsReadOnlyVerb(VerbChain verbChain)
    {
        if (verbChain.Tokens.Count == 0)
            return false;

        // Try the longest verb-chain join first (e.g. "git status" must hit
        // the list before "git" would, but the safe-verbs list contains
        // multi-token entries verbatim so equality covers both cases).
        return _readOnlyVerbs.Contains(verbChain.Joined);
    }

    /// <summary>
    /// True when the parsed clause matches any verb pattern in the composed
    /// state. Pattern format: verb-chain prefix + trailing arg-glob suffix
    /// (e.g. <c>git push *</c>). Matching is case-insensitive on the verb
    /// chain.
    /// </summary>
    public bool MatchesVerbPattern(VerbChain verbChain, IReadOnlyList<Arg> args)
    {
        if (verbChain.Tokens.Count == 0)
            return false;

        var verbJoined = verbChain.Joined;
        var argTokens = args.Where(a => !a.IsCwdAttribution)
            .Select(a => a.Raw)
            .ToList();

        foreach (var pattern in _allVerbPatterns)
        {
            if (MatchesPatternInternal(pattern, verbJoined, argTokens))
                return true;
        }

        return false;
    }

    /// <summary>The composed set of normalized trusted zone globs.</summary>
    public IReadOnlyCollection<string> AllTrustedZones => _normalizedZones;

    /// <summary>The composed set of verb-pattern globs.</summary>
    public IReadOnlyCollection<string> AllVerbPatterns => _allVerbPatterns;

    private static bool MatchesPatternInternal(string pattern, string verbJoined, IReadOnlyList<string> argTokens)
    {
        // Split pattern into [verb-chain-prefix, arg-glob-suffix].
        // AudienceTrustStore.NormalizeVerbPattern guarantees a trailing
        // arg-glob token; we still defend against malformed patterns by
        // matching only when the split succeeds.
        var lastSpace = pattern.LastIndexOf(' ');
        if (lastSpace <= 0)
            return false;

        var patternVerb = pattern[..lastSpace].Trim();
        var argGlob = pattern[(lastSpace + 1)..].Trim();

        // Verb chain must match exactly (case-insensitive).
        if (!string.Equals(patternVerb, verbJoined, StringComparison.OrdinalIgnoreCase))
            return false;

        // Arg glob: '*' matches any args; specific path globs (e.g. /tmp/*)
        // require at least one matching arg token. v0.1 supports only '*'
        // as the universal glob and exact-prefix path globs. Anything more
        // expressive is deferred — operators wanting precision can use the
        // narrower CLI form `verb specific-arg *`.
        if (argGlob == "*")
            return true;

        // Path-prefix glob (e.g. `/tmp/*`): any arg whose resolved path
        // starts with the prefix matches.
        if (argGlob.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = argGlob[..^2];
            foreach (var token in argTokens)
            {
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Literal arg match.
        foreach (var token in argTokens)
        {
            if (string.Equals(token, argGlob, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeZone(string? raw, string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var trimmed = raw.Trim();

        // Strip trailing `/*` — the `*` is implicit and recursive per the
        // locked design, not a literal glob.
        if (trimmed.EndsWith("/*", StringComparison.Ordinal)
            || trimmed.EndsWith("\\*", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^2];
        }

        // Strip trailing slashes so /etc/nginx and /etc/nginx/ compare
        // equal.
        while (trimmed.Length > 1 && (trimmed[^1] == '/' || trimmed[^1] == '\\'))
            trimmed = trimmed[..^1];

        // Expand `~` to the daemon-process user's home (or the explicit
        // override). `$HOME` is also accepted as a literal prefix for
        // forward-compat with future tilde-equivalents.
        if (trimmed == "~")
        {
            return homeDirectory;
        }

        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(homeDirectory, trimmed[2..]);
        }

        if (trimmed.StartsWith("$HOME/", StringComparison.Ordinal))
        {
            return Path.Combine(homeDirectory, trimmed[6..]);
        }

        return trimmed;
    }

    private static bool IsSamePathOrChild(string candidate, string zone)
    {
        if (!candidate.StartsWith(zone, StringComparison.OrdinalIgnoreCase))
            return false;

        if (candidate.Length == zone.Length)
            return true;

        var boundary = candidate[zone.Length];
        return boundary == '/' || boundary == '\\';
    }
}
