// -----------------------------------------------------------------------
// <copyright file="TrustStateComposer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Security;

/// <summary>
/// Composes a <see cref="TrustState"/> for one gate evaluation from the four
/// trust sources: audience baseline (from <c>netclaw.json</c> trust
/// profiles), persisted store (<see cref="AudienceTrustStore"/>), in-memory
/// session-scope grants (from <c>LlmSessionActor</c>), and the immutable
/// session directory.
/// </summary>
/// <remarks>
/// Pure function — no internal mutable state. The audience profiles and
/// AudienceTrustStore singletons are captured at construction; per-call
/// session inputs (session_dir + session-scope grants) flow through
/// <see cref="Compose"/>.
///
/// Audience baseline trusted zones derive from the audience profile's
/// <see cref="ToolFilesystemAccessProfile.Roots"/> on <c>ReadFiles</c>, and
/// <see cref="ToolFilesystemAccessProfile.Mode"/> drives whether the zone
/// gate trusts arbitrary paths (<c>Mode=All</c> → trust everything;
/// <c>Mode=Roots</c> → trust only the listed roots; <c>Mode=None</c> →
/// trust nothing beyond session-scope grants and the session directory).
/// The composer does not consult <c>WriteFiles.Roots</c> separately — the
/// zone gate treats all path operands uniformly (read or write distinction
/// is the verb gate's concern). Operators wanting per-write restrictions
/// configure that via the hard-deny rule set or specific verb patterns,
/// not via the zone composer.
/// </remarks>
public sealed class TrustStateComposer
{
    private readonly ToolAudienceProfiles _profiles;
    private readonly AudienceTrustStore _store;
    private readonly SafeVerbList _safeVerbs;
    private readonly string? _homeDirectoryOverride;

    public TrustStateComposer(
        ToolAudienceProfiles profiles,
        AudienceTrustStore store,
        SafeVerbList safeVerbs,
        string? homeDirectoryOverride = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _safeVerbs = safeVerbs ?? throw new ArgumentNullException(nameof(safeVerbs));
        _homeDirectoryOverride = homeDirectoryOverride;
    }

    /// <summary>
    /// Builds a <see cref="TrustState"/> for the audience evaluating one
    /// shell call. Session-scope inputs come from the <c>LlmSessionActor</c>
    /// at call time; the rest snapshot from the captured config and store
    /// state.
    /// </summary>
    public TrustState Compose(
        TrustAudience audience,
        string sessionDirectory,
        IEnumerable<string>? sessionTrustedZones = null,
        IEnumerable<string>? sessionVerbPatterns = null)
    {
        var profile = ResolveProfile(audience);
        var baselineZones = profile.ReadFiles.Roots;

        // Mode=All on the audience's read-files profile is the operator
        // declaring "trust the whole filesystem for this audience" — Roots
        // is meaningless in that mode, so without this flag the composer
        // would hand TrustState an empty zone set and the gate would prompt
        // on every path operand. Mode=Roots and Mode=None still rely on
        // the explicit Roots list (None typically empty).
        var trustsAllPaths = profile.ReadFiles.Mode == ToolFilesystemMode.All;

        return new TrustState(
            baselineZones: baselineZones,
            persistedZones: _store.GetTrustedZones(audience),
            sessionZones: sessionTrustedZones ?? [],
            sessionDirectory: sessionDirectory,
            persistedVerbPatterns: _store.GetVerbPatterns(audience),
            sessionVerbPatterns: sessionVerbPatterns ?? [],
            readOnlyVerbs: _safeVerbs,
            homeDirectory: _homeDirectoryOverride,
            trustsAllPaths: trustsAllPaths);
    }

    private ToolAudienceProfile ResolveProfile(TrustAudience audience)
        => audience switch
        {
            TrustAudience.Personal => _profiles.Personal,
            TrustAudience.Team => _profiles.Team,
            TrustAudience.Public => _profiles.Public,
            _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Unknown trust audience.")
        };
}
