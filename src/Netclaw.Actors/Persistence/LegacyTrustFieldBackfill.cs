// -----------------------------------------------------------------------
// <copyright file="LegacyTrustFieldBackfill.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Actors.Persistence;

/// <summary>
/// Backfills trust fields on legacy persisted job/reminder JSON documents that
/// predate the type-system-stiffening change (issue #994), which made
/// <c>Audience</c> and <c>Boundary</c> required.
///
/// A pre-upgrade document either omits these keys entirely or carries an
/// explicit <c>null</c> (the fields used to be nullable). Either way the
/// document would fail to deserialize into the now-required members. Rather
/// than throw — which would force an operator migration — the store rewrites
/// such a document in memory before deserialization, injecting a conservative
/// fail-closed value (<c>Public</c> audience, public boundary). The
/// substitution is loud (a logged warning) and cannot escalate: <c>Public</c>
/// is the least-privileged audience, so a backfilled job or reminder runs with
/// fewer grants, never more.
/// </summary>
internal static class LegacyTrustFieldBackfill
{
    private const string AudienceKey = "audience";
    private const string BoundaryKey = "boundary";

    /// <summary>
    /// Returns <paramref name="json"/> unchanged when it already carries both
    /// trust fields, or a rewritten document with conservative fail-closed
    /// values injected for whichever trust field is absent or null. Logs one
    /// warning naming <paramref name="documentPath"/> when a backfill occurs.
    /// </summary>
    public static string ApplyIfNeeded(string json, string documentPath, ILogger logger)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
            return json;

        var backfilledAudience = IsAbsentOrNull(root, AudienceKey);
        var backfilledBoundary = IsAbsentOrNull(root, BoundaryKey);
        if (!backfilledAudience && !backfilledBoundary)
            return json;

        if (backfilledAudience)
            root[AudienceKey] = TrustAudience.Public.ToString();
        if (backfilledBoundary)
            root[BoundaryKey] = SecurityPolicyDefaults.PublicBoundary;

        logger.LogWarning(
            "Legacy persisted document {DocumentPath} is missing trust fields "
            + "(audience backfilled: {BackfilledAudience}, boundary backfilled: {BackfilledBoundary}); "
            + "applying fail-closed Public defaults. The document predates issue #994.",
            documentPath, backfilledAudience, backfilledBoundary);

        return root.ToJsonString();
    }

    // Web-serialized documents use camelCase keys; deserialization is
    // case-insensitive, so a case-sensitive absence check on the camelCase key
    // is sufficient — but an older document could also carry an explicit null.
    private static bool IsAbsentOrNull(JsonObject root, string key)
        => !root.TryGetPropertyValue(key, out var value) || value is null;
}
