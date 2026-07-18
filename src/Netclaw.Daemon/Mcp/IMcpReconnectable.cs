// -----------------------------------------------------------------------
// <copyright file="IMcpReconnectable.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal interface IMcpReconnectable
{
    IReadOnlyDictionary<McpServerName, McpServerStatus> GetServerStatuses();

    Task<bool> TryReconnectAsync(McpServerName serverName, CancellationToken ct = default);

    /// <summary>
    /// Proactively refreshes OAuth tokens for connected, OAuth-managed MCP
    /// servers ahead of expiry, and surfaces advance warnings for token sets
    /// that cannot self-heal (no refresh token, no known expiry). Called once
    /// per <see cref="McpReconnectionService"/> tick.
    /// </summary>
    Task RefreshOAuthTokensAsync(CancellationToken ct = default);
}
