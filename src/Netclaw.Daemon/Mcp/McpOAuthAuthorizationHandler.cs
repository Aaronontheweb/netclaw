// -----------------------------------------------------------------------
// <copyright file="McpOAuthAuthorizationHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Makes <see cref="McpOAuthService"/> the sole runtime owner of MCP OAuth
/// refresh. The MCP SDK's OAuth provider cannot participate here because it
/// redeems cached refresh tokens outside Netclaw's per-server single-flight.
/// </summary>
internal sealed class McpOAuthAuthorizationHandler : DelegatingHandler
{
    private readonly McpServerName _serverName;
    private readonly McpServerEntry _entry;
    private readonly McpOAuthService _oauthService;

    public McpOAuthAuthorizationHandler(
        McpServerName serverName,
        McpServerEntry entry,
        McpOAuthService oauthService,
        HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _serverName = serverName;
        _entry = entry;
        _oauthService = oauthService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var accessToken = await _oauthService.GetValidTokenAsync(
            _serverName, _entry, cancellationToken);

        // A transient proactive refresh failure retains the prior token. Send
        // it once so a provider that still accepts it can keep serving traffic;
        // any 401 below goes through the logged forced-refresh path.
        accessToken ??= _oauthService.GetTokenSet(_serverName)?.AccessToken.Value;

        if (accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var retryRequest = await CloneAsync(request, cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Unauthorized || accessToken is null)
            return response;

        string? replacementToken;
        try
        {
            replacementToken = await _oauthService.RefreshAfterUnauthorizedAsync(
                _serverName, _entry, accessToken, cancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        if (replacementToken is null)
            return response;

        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", replacementToken);
        response.Dispose();
        return await base.SendAsync(retryRequest, cancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in request.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);

        if (request.Content is not null)
        {
            var content = new ByteArrayContent(
                await request.Content.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in request.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }

        return clone;
    }
}
