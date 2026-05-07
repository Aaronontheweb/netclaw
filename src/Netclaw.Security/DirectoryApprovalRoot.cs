// -----------------------------------------------------------------------
// <copyright file="DirectoryApprovalRoot.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Security;

/// <summary>
/// Human-facing and comparison-safe representation of a directory approval root.
/// <see cref="DisplayPath"/> preserves the shape we want to show back to the
/// user (which may stay relative if that is how the command was written), while
/// <see cref="ComparisonRoot"/> is the normalized root used for approval-store
/// lookups and containment checks.
/// </summary>
public sealed record DirectoryApprovalRoot(string DisplayPath, string ComparisonRoot);
