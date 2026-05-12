// -----------------------------------------------------------------------
// <copyright file="SessionScopeGrants.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Sessions;

/// <summary>
/// In-memory store for trust-zones session-scope grants on a single
/// <see cref="LlmSessionActor"/> instance. The user's "Session" click on
/// either the zone gate or the verb gate writes here; lookups join this
/// with audience-baseline and persisted-store entries when the gate
/// evaluator composes a <c>TrustState</c> for the next call.
/// </summary>
/// <remarks>
/// Session-scope is the only approval scope that is NOT persisted: by
/// design, entries here disappear when the actor is shut down or
/// recovered from snapshot. The actor exposes this as a transient field
/// alongside <c>_buffer</c> / <c>_inFlightReminderIds</c> / etc., never
/// part of <c>SessionState</c> / <c>SessionSnapshot</c>. A structural
/// test pins that boundary so a future refactor can't silently move
/// session-scope grants into the persisted path.
///
/// Verb patterns are matched case-insensitively (verb chains and arg
/// globs are case-insensitive on POSIX too — that's the existing
/// contract from <c>ApprovalPatternMatching</c>). Trusted zones use
/// the platform-correct comparer from <c>ToolApprovalEntryComparer</c>
/// so a planted directory under a case-sensitive filesystem can't
/// be redeemed by a casing variant.
/// </remarks>
internal sealed class SessionScopeGrants
{
    private readonly HashSet<string> _trustedZones = new(
        Netclaw.Configuration.ToolApprovalEntryComparer.Comparer);

    private readonly HashSet<string> _verbPatterns = new(
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshot of the trusted-zone globs the user has accepted at
    /// Session scope. Read-only view; mutations go through
    /// <see cref="AddTrustedZone"/>.
    /// </summary>
    public IReadOnlyCollection<string> TrustedZones => _trustedZones;

    /// <summary>
    /// Snapshot of the verb-pattern globs (e.g. <c>git push origin main *</c>,
    /// <c>curl *</c>) the user has accepted at Session scope.
    /// </summary>
    public IReadOnlyCollection<string> VerbPatterns => _verbPatterns;

    /// <summary>
    /// Records a trusted-zone grant for the lifetime of this actor
    /// instance. Whitespace-only input is rejected; surrounding
    /// whitespace is trimmed. Returns <c>true</c> if a new entry was
    /// added; <c>false</c> if rejected or already present.
    /// </summary>
    public bool AddTrustedZone(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
            return false;

        return _trustedZones.Add(zone.Trim());
    }

    /// <summary>
    /// Records a verb-pattern grant for the lifetime of this actor
    /// instance. Whitespace-only input is rejected; surrounding
    /// whitespace is trimmed. Returns <c>true</c> if a new entry was
    /// added; <c>false</c> if rejected or already present.
    /// </summary>
    public bool AddVerbPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        return _verbPatterns.Add(pattern.Trim());
    }
}
