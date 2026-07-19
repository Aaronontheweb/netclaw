// -----------------------------------------------------------------------
// <copyright file="McpOAuthService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers.OAuth;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Orchestrates the OAuth 2.1 Authorization Code + PKCE lifecycle for MCP servers.
/// Handles discovery, DCR, token persistence, and automatic token refresh.
/// Delegates PKCE generation, authorization URL construction, and token exchange
/// to <see cref="OAuthPkceService"/>.
/// </summary>
internal sealed class McpOAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpOAuthService> _logger;
    private readonly OAuthPkceService _pkceService;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly ISecretsProtector? _protector;

    // In-memory caches, loaded from disk at construction
    private readonly ConcurrentDictionary<McpServerName, McpOAuthTokenSet> _tokens = new();
    private readonly ConcurrentDictionary<McpServerName, McpOAuthServerMetadata> _metadata = new();

    // Maps PKCE state → flow context for token persistence after callback
    private readonly ConcurrentDictionary<string, McpOAuthFlowContext> _stateToContext = new();

    // Maps PKCE state → server name for recently completed flows (for auto-reconnect)
    private readonly ConcurrentDictionary<string, McpServerName> _lastCompletedServerName = new();

    public McpOAuthService(
        HttpClient httpClient,
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<McpOAuthService> logger,
        OAuthPkceService pkceService,
        IOperationalNotificationSink notificationSink,
        ISecretsProtector? protector = null)
    {
        _httpClient = httpClient;
        _paths = paths;
        _timeProvider = timeProvider;
        _logger = logger;
        _pkceService = pkceService;
        _notificationSink = notificationSink;
        _protector = protector;

        LoadTokensFromDisk();
        LoadMetadataFromDisk();
    }

    // ── Discovery ──────────────────────────────────────────────────────

    /// <summary>
    /// Auto-detect whether an MCP server requires OAuth by probing its
    /// well-known metadata endpoints. Returns null if no OAuth is needed.
    /// </summary>
    public async Task<McpOAuthServerMetadata?> TryDiscoverMetadataAsync(
        McpServerName serverName, string serverUrl, CancellationToken ct)
    {
        serverUrl = serverUrl.TrimEnd('/');

        // Check cache (1-hour TTL)
        if (_metadata.TryGetValue(serverName, out var cached)
            && (_timeProvider.GetUtcNow() - cached.CachedAt).TotalHours < 1)
        {
            return cached;
        }

        // 1. Probe the MCP server URL for a 401 with WWW-Authenticate
        string? resourceMetadataUri = null;
        try
        {
            using var probeRequest = new HttpRequestMessage(HttpMethod.Get, serverUrl);
            using var probeResponse = await _httpClient.SendAsync(probeRequest, ct);

            if (probeResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && probeResponse.Headers.WwwAuthenticate.Count > 0)
            {
                foreach (var auth in probeResponse.Headers.WwwAuthenticate)
                {
                    if (auth.Parameter is not null
                        && auth.Parameter.Contains("resource_metadata", StringComparison.Ordinal))
                    {
                        resourceMetadataUri = ExtractQuotedParam(auth.Parameter, "resource_metadata");
                    }
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Probe of {ServerUrl} failed (expected for OAuth servers)", serverUrl);
        }

        // 2. Resolve protected resource metadata
        string? authServerUrl;
        string? resourceIndicator = null;

        if (resourceMetadataUri is not null)
        {
            var resourceMeta = await _httpClient.GetFromJsonAsync<JsonElement>(resourceMetadataUri, ct);
            authServerUrl = resourceMeta.GetProperty("authorization_servers")[0].GetString();
            if (resourceMeta.TryGetProperty("resource", out var resProp))
                resourceIndicator = resProp.GetString();
        }
        else
        {
            // Fallback: try well-known path on the MCP server origin
            var origin = new Uri(serverUrl).GetLeftPart(UriPartial.Authority);
            var wellKnownUrl = $"{origin}/.well-known/oauth-protected-resource";
            try
            {
                var resourceMeta = await _httpClient.GetFromJsonAsync<JsonElement>(wellKnownUrl, ct);
                authServerUrl = resourceMeta.GetProperty("authorization_servers")[0].GetString();
                if (resourceMeta.TryGetProperty("resource", out var resProp))
                    resourceIndicator = resProp.GetString();
            }
            catch (Exception ex)
            {
                // No OAuth metadata found — server doesn't require OAuth
                _logger.LogDebug(ex, "No OAuth metadata at {Url} — server does not require OAuth", wellKnownUrl);
                return null;
            }
        }

        if (string.IsNullOrEmpty(authServerUrl))
            return null;

        // 3. Resolve auth server metadata
        var authMetaUrl = $"{authServerUrl.TrimEnd('/')}/.well-known/oauth-authorization-server";
        var authMeta = await _httpClient.GetFromJsonAsync<JsonElement>(authMetaUrl, ct);

        var metadata = new McpOAuthServerMetadata
        {
            McpServerUrl = serverUrl,
            AuthorizationEndpoint = authMeta.GetProperty("authorization_endpoint").GetString()!,
            TokenEndpoint = authMeta.GetProperty("token_endpoint").GetString()!,
            RegistrationEndpoint = authMeta.TryGetProperty("registration_endpoint", out var regProp)
                ? regProp.GetString() : null,
            ResourceIndicator = resourceIndicator ?? serverUrl,
            CachedAt = _timeProvider.GetUtcNow(),
        };

        _metadata[serverName] = metadata;
        PersistMetadata();

        return metadata;
    }

    // ── Client Registration (RFC 7591 DCR) ─────────────────────────────

    /// <summary>
    /// Ensure a client_id is available for this server. Uses the static
    /// <see cref="McpServerEntry.OAuthClientId"/> if set, otherwise attempts
    /// dynamic client registration.
    /// </summary>
    public async Task<string> EnsureClientRegisteredAsync(
        McpServerName serverName, McpServerEntry entry, McpOAuthServerMetadata metadata, CancellationToken ct)
    {
        // Static client ID takes precedence
        if (!string.IsNullOrWhiteSpace(entry.OAuthClientId))
        {
            metadata.ClientId = entry.OAuthClientId;
            _metadata[serverName] = metadata;
            PersistMetadata();
            return entry.OAuthClientId;
        }

        // Already registered?
        if (!string.IsNullOrWhiteSpace(metadata.ClientId))
            return metadata.ClientId;

        // Dynamic client registration
        if (string.IsNullOrWhiteSpace(metadata.RegistrationEndpoint))
            throw new InvalidOperationException(
                $"MCP server '{serverName.Value}' requires OAuth but no client_id is configured " +
                "and the auth server doesn't support dynamic client registration. " +
                $"Set OAuthClientId in the MCP server config.");

        var dcrPayload = new
        {
            client_name = "netclaw",
            redirect_uris = new[] { "http://127.0.0.1:5199/api/mcp/oauth/callback" },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        };

        var response = await _httpClient.PostAsJsonAsync(metadata.RegistrationEndpoint, dcrPayload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var clientId = result.GetProperty("client_id").GetString()
            ?? throw new InvalidOperationException("DCR response missing client_id");

        metadata.ClientId = clientId;
        _metadata[serverName] = metadata;
        PersistMetadata();

        _logger.LogInformation("Registered OAuth client for MCP server '{Name}': {ClientId}", serverName.Value, clientId);
        return clientId;
    }

    // ── Start Authorization Flow ───────────────────────────────────────

    /// <summary>
    /// Discover metadata, ensure client registration, then delegate to
    /// <see cref="OAuthPkceService"/> for PKCE generation and URL construction.
    /// </summary>
    public async Task<(string AuthorizationUrl, string State)> StartAuthorizationFlowAsync(
        McpServerName serverName, McpServerEntry entry, CancellationToken ct)
    {
        var metadata = await TryDiscoverMetadataAsync(serverName, entry.Url!, ct)
            ?? throw new InvalidOperationException(
                $"MCP server '{serverName.Value}' does not advertise OAuth metadata. " +
                "No /.well-known/oauth-protected-resource was found.");
        var clientId = await EnsureClientRegisteredAsync(serverName, entry, metadata, ct);

        var redirectUri = "http://127.0.0.1:5199/api/mcp/oauth/callback";

        // Build extra params for authorization URL (resource indicator)
        Dictionary<string, string>? extraAuthParams = null;
        if (!string.IsNullOrWhiteSpace(metadata.ResourceIndicator))
        {
            extraAuthParams = new Dictionary<string, string>
            {
                ["resource"] = metadata.ResourceIndicator,
            };
        }

        // Build extra params for token exchange/refresh (resource indicator per RFC 8707)
        Dictionary<string, string>? extraTokenParams = null;
        if (!string.IsNullOrWhiteSpace(metadata.ResourceIndicator))
        {
            extraTokenParams = new Dictionary<string, string>
            {
                ["resource"] = metadata.ResourceIndicator,
            };
        }

        var (authUrl, state) = _pkceService.StartAuthorizationFlow(
            metadata.AuthorizationEndpoint,
            metadata.TokenEndpoint,
            clientId,
            redirectUri,
            entry.OAuthScope,
            extraAuthParams,
            extraTokenParams);

        // Store context so we can persist tokens when the callback arrives
        _stateToContext[state] = new McpOAuthFlowContext(
            serverName, clientId, metadata.ResourceIndicator, _timeProvider.GetUtcNow());

        // Prune abandoned flows older than 10 minutes
        PruneStaleFlowContexts();

        _logger.LogInformation("Started OAuth flow for MCP server '{Name}' (state={State})", serverName.Value, state);
        return (authUrl, state);
    }

    // ── Complete Authorization Flow (callback) ─────────────────────────

    /// <summary>
    /// Exchange the authorization code for tokens via <see cref="OAuthPkceService"/>,
    /// then persist the result as an <see cref="McpOAuthTokenSet"/>.
    /// </summary>
    public async Task CompleteAuthorizationAsync(string code, string state, CancellationToken ct)
    {
        if (!_stateToContext.TryGetValue(state, out var context))
            throw new InvalidOperationException($"Unknown OAuth state: {state}");

        try
        {
            var result = await _pkceService.CompleteAuthorizationAsync(code, state, ct);

            // Convert OAuthDeviceFlowResult → McpOAuthTokenSet for persistence
            var tokenSet = new McpOAuthTokenSet
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                ExpiresAt = result.ExpiresAt,
                ClientId = context.ClientId,
                McpServerUrl = context.ResourceIndicator,
            };

            _tokens[context.ServerName] = tokenSet;
            PersistTokens();

            // A fresh grant supersedes any prior terminal rejection, and its
            // advisory conditions (missing refresh token / unknown expiry)
            // should be re-evaluated — and re-warned if still true.
            _terminalRefreshErrors.TryRemove(context.ServerName, out _);
            _warnedNoRefreshToken.TryRemove(context.ServerName, out _);
            _warnedUnknownExpiry.TryRemove(context.ServerName, out _);

            // Cache the server name so callers can trigger reconnect after cleanup
            _lastCompletedServerName[state] = context.ServerName;

            _logger.LogInformation("OAuth flow completed for MCP server '{Name}'", context.ServerName.Value);
        }
        finally
        {
            // Always clean up context — whether success or failure
            _stateToContext.TryRemove(state, out _);
        }
    }

    /// <summary>
    /// Returns the server name for a recently completed OAuth flow (by state).
    /// Used to trigger auto-reconnect after token acquisition.
    /// </summary>
    public McpServerName? GetServerNameForState(string state)
    {
        if (_lastCompletedServerName.TryRemove(state, out var name))
            return name;
        return null;
    }

    // ── Token Access ───────────────────────────────────────────────────

    /// <summary>
    /// How far ahead of the persisted <see cref="McpOAuthTokenSet.ExpiresAt"/> to
    /// refresh proactively, rather than waiting for the SDK's own on-401 fallback
    /// (which is silent, and on our headless wiring terminates in a generic
    /// failure — see <c>ClientOAuthProvider</c>). Ten minutes comfortably spans
    /// several <see cref="McpReconnectionService"/> ticks (30s each) plus the
    /// refresh request itself and clock skew against the provider.
    /// </summary>
    internal static readonly TimeSpan ProactiveRefreshWindow = TimeSpan.FromMinutes(10);

    // Per-server refresh single-flight. Providers such as Notion rotate the
    // refresh token on every redemption and treat concurrent redemption of the
    // same refresh token as theft, revoking the whole grant — so at most one
    // refresh call per server may be in flight at a time.
    private readonly ConcurrentDictionary<McpServerName, SemaphoreSlim> _refreshGates = new();

    // De-dupe advisory warnings that would otherwise re-log on every
    // proactive-refresh tick (every 30s) for a server stuck in a state the
    // operator already knows about. Cleared once the underlying condition is
    // no longer true (e.g. a fresh refresh token or expiry is observed).
    private readonly ConcurrentDictionary<McpServerName, byte> _warnedNoRefreshToken = new();
    private readonly ConcurrentDictionary<McpServerName, byte> _warnedUnknownExpiry = new();

    // OAuth error code of the last terminal refresh rejection per server, so
    // the connection manager can surface the specific code (invalid_grant vs
    // invalid_client vs unauthorized_client) in the server status. Written
    // just before the tokens are cleared; removed when the operator completes
    // a fresh auth flow.
    private readonly ConcurrentDictionary<McpServerName, string> _terminalRefreshErrors = new();

    /// <summary>
    /// True when the server's last refresh attempt was terminally rejected;
    /// <paramref name="errorCode"/> carries the OAuth error code. Callers
    /// should pair this with a tokens-cleared check (<see cref="GetTokenSet"/>
    /// returning null) — the record intentionally lingers until re-auth so the
    /// distinct status survives reconnect attempts.
    /// </summary>
    internal bool TryGetTerminalRefreshRejection(
        McpServerName serverName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? errorCode)
        => _terminalRefreshErrors.TryGetValue(serverName, out errorCode);

    /// <summary>
    /// Get a valid access token for the given MCP server, refreshing proactively
    /// (ahead of <see cref="McpOAuthTokenSet.ExpiresAt"/> by <see cref="ProactiveRefreshWindow"/>)
    /// as well as reactively (already expired). Refreshes are single-flighted
    /// per server. Returns null if no token is cached, no refresh token is
    /// available, or the refresh attempt failed.
    /// </summary>
    public async Task<string?> GetValidTokenAsync(
        McpServerName serverName, McpServerEntry entry, CancellationToken ct)
    {
        if (!_tokens.TryGetValue(serverName, out var tokenSet))
            return null;

        if (!NeedsRefresh(tokenSet))
            return tokenSet.AccessToken.Value;

        var gate = _refreshGates.GetOrAdd(serverName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-read and re-check under the gate: a concurrent caller may have
            // already refreshed — or a terminal invalid_grant may have just
            // cleared the tokens entirely — while we were waiting.
            if (!_tokens.TryGetValue(serverName, out tokenSet))
                return null;

            if (!NeedsRefresh(tokenSet))
                return tokenSet.AccessToken.Value;

            if (tokenSet.RefreshToken is null)
            {
                WarnNoRefreshToken(serverName, tokenSet.ExpiresAt);
                return null;
            }

            return await RefreshTokenAsync(serverName, entry, tokenSet, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Refreshes the access token after the MCP resource server rejected the
    /// specific token carried by a request. Concurrent 401 responses for the
    /// same token coalesce under the normal per-server refresh gate; callers
    /// waiting behind the winner reuse its replacement token.
    /// </summary>
    public async Task<string?> RefreshAfterUnauthorizedAsync(
        McpServerName serverName,
        McpServerEntry entry,
        string rejectedAccessToken,
        CancellationToken ct)
    {
        var gate = _refreshGates.GetOrAdd(serverName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!_tokens.TryGetValue(serverName, out var tokenSet))
                return null;

            // A concurrent request already redeemed the refresh token. Reuse
            // its result instead of presenting the rotated credential again.
            if (!string.Equals(tokenSet.AccessToken.Value, rejectedAccessToken, StringComparison.Ordinal))
                return tokenSet.AccessToken.Value;

            if (tokenSet.RefreshToken is null)
            {
                WarnNoRefreshToken(serverName, tokenSet.ExpiresAt);
                return null;
            }

            return await RefreshTokenAsync(serverName, entry, tokenSet, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool NeedsRefresh(McpOAuthTokenSet tokenSet)
        => tokenSet.ExpiresAt is not null
           && tokenSet.ExpiresAt.Value - ProactiveRefreshWindow <= _timeProvider.GetUtcNow();

    /// <summary>
    /// Structural check, independent of proactive-refresh timing: if a token
    /// set is time-limited but has no refresh token, silent refresh is
    /// impossible no matter how far off expiry is. Called at connect time and
    /// on every proactive-refresh tick so operators get advance warning instead
    /// of discovering it only once the token has already expired.
    /// </summary>
    public void WarnIfMissingRefreshToken(McpServerName serverName)
    {
        if (!_tokens.TryGetValue(serverName, out var tokenSet) || tokenSet.ExpiresAt is null)
            return;

        if (tokenSet.RefreshToken is not null)
        {
            _warnedNoRefreshToken.TryRemove(serverName, out _);
            return;
        }

        WarnNoRefreshToken(serverName, tokenSet.ExpiresAt);
    }

    /// <summary>
    /// Notes (once) that a server's token set has no known expiry, so proactive
    /// refresh cannot be scheduled for it — only the reactive on-401 path
    /// applies. Called from the proactive-refresh sweep only; unlike
    /// <see cref="WarnIfMissingRefreshToken"/> this is not connect-time
    /// guidance, it is a guard against silently skipping proactive refresh.
    /// </summary>
    public void NoteUnknownExpiryOnce(McpServerName serverName)
    {
        if (!_tokens.TryGetValue(serverName, out var tokenSet))
            return;

        if (tokenSet.ExpiresAt is not null)
        {
            _warnedUnknownExpiry.TryRemove(serverName, out _);
            return;
        }

        if (!_warnedUnknownExpiry.TryAdd(serverName, 0))
            return;

        _logger.LogWarning(
            "MCP server '{Name}' token set has no known expiry; cannot schedule proactive refresh ahead of time — reactive (on-401) refresh still applies",
            serverName.Value);
    }

    private void WarnNoRefreshToken(McpServerName serverName, DateTimeOffset? expiresAt)
    {
        if (!_warnedNoRefreshToken.TryAdd(serverName, 0))
            return;

        _logger.LogWarning(
            "MCP server '{Name}' has no refresh token; silent refresh is impossible and manual re-authorization will be required at expiry ({ExpiresAt})",
            serverName.Value, expiresAt);

        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "mcp.auth.expired",
            AlertType.McpAuthExpired,
            $"MCP server '{serverName.Value}' has no refresh token; access token expires at {expiresAt:u} and silent refresh is impossible. Run: netclaw mcp auth {serverName.Value}",
            AlertSeverity.Warning,
            source: serverName.Value,
            context: new Dictionary<string, string>
            {
                ["serverName"] = serverName.Value,
                ["reason"] = "no_refresh_token",
            }));
    }

    // ── Token Refresh ──────────────────────────────────────────────────

    private async Task<string?> RefreshTokenAsync(
        McpServerName serverName, McpServerEntry entry, McpOAuthTokenSet tokenSet, CancellationToken ct)
    {
        if (!_metadata.TryGetValue(serverName, out var metadata))
        {
            _logger.LogWarning("No cached metadata for MCP server '{Name}', cannot refresh token", serverName.Value);
            return null;
        }

        try
        {
            // Build extra params for resource indicator
            Dictionary<string, string>? extraParams = null;
            if (!string.IsNullOrWhiteSpace(metadata.ResourceIndicator))
            {
                extraParams = new Dictionary<string, string>
                {
                    ["resource"] = metadata.ResourceIndicator,
                };
            }

            var result = await _pkceService.RefreshTokenAsync(
                metadata.TokenEndpoint,
                tokenSet.ClientId ?? entry.OAuthClientId ?? "",
                tokenSet.RefreshToken!,
                extraParams,
                ct);

            var newTokenSet = new McpOAuthTokenSet
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken ?? tokenSet.RefreshToken,
                ExpiresAt = result.ExpiresAt,
                ClientId = tokenSet.ClientId,
                McpServerUrl = tokenSet.McpServerUrl,
            };

            _tokens[serverName] = newTokenSet;
            PersistTokens();
            _warnedNoRefreshToken.TryRemove(serverName, out _);
            _warnedUnknownExpiry.TryRemove(serverName, out _);

            _logger.LogInformation("Token refreshed for MCP server '{Name}'", serverName.Value);
            return newTokenSet.AccessToken.Value;
        }
        catch (OAuthTokenRefreshRejectedException ex)
        {
            // Terminal per RFC 6749 §5.2 — one shared path for all three codes,
            // with the code carried as data. invalid_grant: the grant was
            // revoked or the rotating refresh token already consumed.
            // invalid_client / unauthorized_client: the client registration
            // itself is dead (e.g. a provider purged its DCR'd client IDs).
            // Either way, retrying with the same credentials can never succeed:
            // clear the stored tokens so callers stop retrying and fail
            // straight to `netclaw mcp auth`.
            _logger.LogWarning(
                "Token refresh rejected for MCP server '{Name}': {ErrorCode} (terminal, HTTP {StatusCode}) — re-authorization required",
                serverName.Value, ex.ErrorCode, (int)ex.StatusCode);

            // Record the code before clearing the tokens: readers pair the
            // rejection record with a tokens-cleared check, so the record must
            // be visible first.
            _terminalRefreshErrors[serverName] = ex.ErrorCode;
            _tokens.TryRemove(serverName, out _);
            PersistTokens();

            if (ex.ErrorCode is "invalid_client" or "unauthorized_client")
                ClearRegisteredClient(serverName);

            _notificationSink.Emit(OperationalAlert.Create(
                _timeProvider,
                "mcp.auth.expired",
                AlertType.McpAuthExpired,
                $"MCP server '{serverName.Value}' token refresh rejected ({ex.ErrorCode}). Run: netclaw mcp auth {serverName.Value}",
                AlertSeverity.Warning,
                source: serverName.Value,
                context: new Dictionary<string, string>
                {
                    ["serverName"] = serverName.Value,
                    ["reason"] = ex.ErrorCode,
                }));

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transient failure: network error, 5xx, or a 4xx without a
            // recognized terminal OAuth error code (surfaced by
            // EnsureSuccessStatusCode). Tokens are retained so the server stays
            // retryable — the next proactive tick or reactive 401 will try again.
            var statusCode = (ex as HttpRequestException)?.StatusCode;
            _logger.LogWarning(ex,
                "Token refresh failed for MCP server '{Name}' (status={StatusCode}); tokens retained, will retry",
                serverName.Value, statusCode?.ToString() ?? "n/a");
            return null;
        }
    }

    /// <summary>
    /// Drops the cached (DCR-issued) client_id after the auth server rejected
    /// the client itself. <see cref="EnsureClientRegisteredAsync"/>
    /// short-circuits on a cached <see cref="McpOAuthServerMetadata.ClientId"/>,
    /// so without this the next `netclaw mcp auth` would reuse the dead
    /// client_id; clearing it forces a fresh dynamic registration. Statically
    /// configured client IDs (<see cref="McpServerEntry.OAuthClientId"/>) take
    /// precedence there and are re-applied regardless.
    /// </summary>
    private void ClearRegisteredClient(McpServerName serverName)
    {
        if (!_metadata.TryGetValue(serverName, out var meta) || string.IsNullOrEmpty(meta.ClientId))
            return;

        meta.ClientId = null;
        _metadata[serverName] = meta;
        PersistMetadata();
        _logger.LogWarning(
            "Cleared cached OAuth client registration for MCP server '{Name}'; the next authorization will re-register",
            serverName.Value);
    }

    /// <summary>Returns the cached token set for the given server, or null.</summary>
    internal McpOAuthTokenSet? GetTokenSet(McpServerName serverName)
        => _tokens.TryGetValue(serverName, out var ts) ? ts : null;

    /// <summary>Returns the cached OAuth metadata for the given server, or null.</summary>
    internal McpOAuthServerMetadata? GetCachedMetadata(McpServerName serverName)
        => _metadata.TryGetValue(serverName, out var m) ? m : null;

    // ── Flow Status (for CLI polling) ──────────────────────────────────

    public McpOAuthFlowStatus GetFlowStatus(McpServerName serverName)
    {
        // An active flow takes precedence over any previously stored token.
        foreach (var ctx in _stateToContext.Values)
        {
            if (ctx.ServerName == serverName)
                return McpOAuthFlowStatus.Pending;
        }

        // Check if there's a completed token
        if (_tokens.ContainsKey(serverName))
            return McpOAuthFlowStatus.Completed;

        return McpOAuthFlowStatus.NotStarted;
    }

    /// <summary>
    /// Get flow status by PKCE state value. Delegates to <see cref="OAuthPkceService"/>.
    /// </summary>
    public McpOAuthFlowStatus GetFlowStatusByState(string state)
    {
        var pkceStatus = _pkceService.GetFlowStatus(state);
        return pkceStatus switch
        {
            OAuthPkceFlowStatus.Completed => McpOAuthFlowStatus.Completed,
            OAuthPkceFlowStatus.Pending => McpOAuthFlowStatus.Pending,
            OAuthPkceFlowStatus.Failed => McpOAuthFlowStatus.Failed,
            _ => McpOAuthFlowStatus.NotStarted,
        };
    }

    // ── Flow Cleanup ───────────────────────────────────────────────────

    private static readonly TimeSpan FlowContextTtl = TimeSpan.FromMinutes(10);

    private void PruneStaleFlowContexts()
    {
        var cutoff = _timeProvider.GetUtcNow() - FlowContextTtl;
        foreach (var (state, ctx) in _stateToContext)
        {
            if (ctx.CreatedAt < cutoff)
            {
                if (_stateToContext.TryRemove(state, out _))
                    _logger.LogDebug("Pruned abandoned OAuth flow context (state={State}, server={Server})", state, ctx.ServerName.Value);
            }
        }
    }

    // ── Private Helpers ────────────────────────────────────────────────

    private static string? ExtractQuotedParam(string headerValue, string paramName)
    {
        var key = paramName + "=\"";
        var start = headerValue.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += key.Length;
        var end = headerValue.IndexOf('"', start);
        if (end < 0) return null;

        return headerValue[start..end];
    }

    // ── Persistence ────────────────────────────────────────────────────
    // Tokens go into secrets.json under "McpOAuthTokens".
    // Metadata stays in its own file (non-secret, cache only).

    private const string TokensSectionKey = "McpOAuthTokens";

    private void LoadTokensFromDisk()
    {
        if (!File.Exists(_paths.SecretsPath)) return;

        try
        {
            var json = File.ReadAllText(_paths.SecretsPath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(TokensSectionKey, out var section))
                return;

            var sectionJson = section.GetRawText();

            // Pre-decrypt all ENC: leaves so STJ can parse non-SensitiveString
            // types (DateTimeOffset?, string?) that were blanket-encrypted by
            // SecretsFileWriter. SensitiveStringJsonConverter already checks for
            // ENC: prefix and skips decryption on plaintext — no double-decrypt.
            if (_protector is not null)
                sectionJson = SecretsFileWriter.DecryptJsonLeaves(sectionJson, _protector);

            var tokens = JsonSerializer.Deserialize<Dictionary<string, McpOAuthTokenSet>>(
                sectionJson, JsonOptions);
            if (tokens is not null)
            {
                foreach (var (key, value) in tokens)
                    _tokens[new McpServerName(key)] = value;

                _logger.LogDebug("Loaded OAuth tokens for {Count} server(s) from {Path}", tokens.Count, _paths.SecretsPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load OAuth tokens from {Path}", _paths.SecretsPath);
        }
    }

    private void LoadMetadataFromDisk()
    {
        if (!File.Exists(_paths.McpOAuthMetadataPath)) return;

        try
        {
            var json = File.ReadAllText(_paths.McpOAuthMetadataPath);
            var metadata = JsonSerializer.Deserialize<Dictionary<string, McpOAuthServerMetadata>>(json, JsonOptions);
            if (metadata is not null)
            {
                foreach (var (key, value) in metadata)
                    _metadata[new McpServerName(key)] = value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load OAuth metadata from {Path}", _paths.McpOAuthMetadataPath);
        }
    }

    internal void PersistTokens()
    {
        try
        {
            // Read existing secrets.json, merge in our tokens section, write back
            Dictionary<string, object> secrets;
            if (File.Exists(_paths.SecretsPath))
            {
                var existing = File.ReadAllText(_paths.SecretsPath);
                secrets = JsonSerializer.Deserialize<Dictionary<string, object>>(existing, JsonOptions)
                    ?? [];
            }
            else
            {
                secrets = [];
            }

            secrets[TokensSectionKey] = JsonSerializer.SerializeToElement(
                _tokens.ToDictionary(kvp => kvp.Key.Value, kvp => kvp.Value), JsonOptions);

            SecretsFileWriter.Write(_paths.SecretsPath, secrets,
                options: JsonOptions, protector: _protector);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist OAuth tokens to {Path}", _paths.SecretsPath);
        }
    }

    private void PersistMetadata()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                _metadata.ToDictionary(kvp => kvp.Key.Value, kvp => kvp.Value), JsonOptions);
            File.WriteAllText(_paths.McpOAuthMetadataPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist OAuth metadata to {Path}", _paths.McpOAuthMetadataPath);
        }
    }
}

internal enum McpOAuthFlowStatus
{
    NotStarted,
    Pending,
    Completed,
    Failed,
}

/// <summary>
/// Tracks per-flow context needed to convert <see cref="OAuthDeviceFlowResult"/>
/// into a persistable <see cref="McpOAuthTokenSet"/> after callback.
/// </summary>
internal sealed record McpOAuthFlowContext(
    McpServerName ServerName,
    string ClientId,
    string? ResourceIndicator,
    DateTimeOffset CreatedAt);
