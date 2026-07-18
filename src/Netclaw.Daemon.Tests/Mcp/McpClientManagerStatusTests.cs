// -----------------------------------------------------------------------
// <copyright file="McpClientManagerStatusTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpClientManagerStatusTests
{
    [Fact]
    public void BuildConnectionFailureStatus_WithoutTokensButWithOAuthHints_ReturnsAwaitingAuth()
    {
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            OAuthClientId = "client-id",
        };

        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("notion"),
            entry,
            new HttpRequestException(httpRequestError: HttpRequestError.Unknown, "Unauthorized", null, HttpStatusCode.Unauthorized),
            hasCachedTokens: false,
            hasOAuthRuntimeHints: true);

        Assert.Equal(McpConnectionState.AwaitingAuth, status.State);
        Assert.Contains("netclaw mcp auth notion", status.ErrorMessage);
    }

    [Fact]
    public void BuildConnectionFailureStatus_WithTokensAndAuthFailure_ReturnsAuthFailed()
    {
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
        };

        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("notion"),
            entry,
            new HttpRequestException(httpRequestError: HttpRequestError.Unknown, "Forbidden", null, HttpStatusCode.Forbidden),
            hasCachedTokens: true,
            hasOAuthRuntimeHints: true);

        Assert.Equal(McpConnectionState.AuthFailed, status.State);
        Assert.Contains("403 Forbidden", status.ErrorMessage);
        Assert.Contains("netclaw mcp auth notion", status.ErrorMessage);
    }

    [Fact]
    public void BuildConnectionFailureStatus_ForNetworkFailure_ReturnsUnreachable()
    {
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
        };

        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("notion"),
            entry,
            new HttpRequestException("Connection refused"),
            hasCachedTokens: false,
            hasOAuthRuntimeHints: false);

        Assert.Equal(McpConnectionState.Unreachable, status.State);
        Assert.Contains("Connection refused", status.ErrorMessage);
    }

    [Fact]
    public void CreateRefreshRejectedStatus_ReturnsAuthFailedWithDistinctInvalidGrantMessage()
    {
        var status = McpClientManager.CreateRefreshRejectedStatus(new McpServerName("notion"));

        // AuthFailed (not Unreachable) so McpReconnectionService's backoff loop
        // — which only retries Unreachable servers — never auto-retries a dead
        // grant into a loop.
        Assert.Equal(McpConnectionState.AuthFailed, status.State);
        Assert.Contains("invalid_grant", status.ErrorMessage);
        Assert.Contains("netclaw mcp auth notion", status.ErrorMessage);

        // Distinct wording from the generic AuthFailed message so operators can
        // tell "grant revoked by provider" apart from "never authenticated".
        var genericAuthFailed = McpClientManager.CreateAuthFailedStatus(
            new McpServerName("notion"),
            new HttpRequestException(httpRequestError: HttpRequestError.Unknown, "Unauthorized", null, System.Net.HttpStatusCode.Unauthorized),
            oauthManaged: true);
        Assert.NotEqual(genericAuthFailed.ErrorMessage, status.ErrorMessage);
    }
}
