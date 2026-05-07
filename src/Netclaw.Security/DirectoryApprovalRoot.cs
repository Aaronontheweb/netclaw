// -----------------------------------------------------------------------
// <copyright file="DirectoryApprovalRoot.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Security;

/// <summary>
/// Human-facing and comparison-safe representation of a directory approval root.
/// </summary>
public sealed record DirectoryApprovalRoot(string DisplayPath, string ComparisonRoot);
