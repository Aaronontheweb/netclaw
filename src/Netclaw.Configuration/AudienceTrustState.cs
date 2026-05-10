// -----------------------------------------------------------------------
// <copyright file="AudienceTrustState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Persistent approval state for one audience: two independent stores
/// composed at evaluation time by the three-layer approval gate.
/// </summary>
/// <remarks>
/// Stores compose at evaluation time, not at write time. A trusted-zone
/// grant authorizes geography only; a verb-pattern grant authorizes a
/// command shape only. Both gates must pass for silent execution.
/// Replaces the v2 <c>ApprovalEntry</c> cross-product shape.
/// </remarks>
public sealed class AudienceTrustState
{
    /// <summary>
    /// Glob patterns matching command shapes auto-allowed within a trusted
    /// zone. Stored as <c>verb-chain prefix + arg-glob suffix</c>
    /// (e.g. <c>git push *</c>, <c>rm /tmp/*</c>, <c>dotnet test *</c>).
    /// Patterns without a trailing glob are rejected at write time.
    /// </summary>
    [JsonPropertyName("verbPatterns")]
    public List<string> VerbPatterns { get; set; } = [];

    /// <summary>
    /// Directory globs declaring where this audience may operate silently.
    /// Path-prefix recursive matching: <c>~/repos/*</c> matches the root
    /// itself plus any descendant at any depth, with directory-boundary
    /// safety (does not match <c>~/repossecret</c>). The <c>*</c> is
    /// implicitly recursive — there is no <c>**</c> in zone globs.
    /// </summary>
    [JsonPropertyName("trustedZones")]
    public List<string> TrustedZones { get; set; } = [];
}
