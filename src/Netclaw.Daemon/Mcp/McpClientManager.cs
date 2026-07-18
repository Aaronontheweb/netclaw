// -----------------------------------------------------------------------
// <copyright file="McpClientManager.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal sealed class McpClientManager : IHostedService, IDisposable, IMcpToolInvoker, IMcpReconnectable
{
    private readonly Dictionary<string, McpServerEntry> _serverEntries;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolConfig _toolConfig;
    private readonly McpOAuthService _oauthService;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpClientManager> _logger;
    private readonly int _maxToolDescriptionChars;
    private readonly int _maxToolSchemaWarnChars;

    private readonly ConcurrentDictionary<McpServerName, McpClient> _clients = new();

    private readonly ConcurrentDictionary<McpServerName, Dictionary<string, AIFunction>> _sharedToolFunctions = new();

    private readonly ConcurrentDictionary<McpServerName, McpServerStatus> _statuses = new();

    // Serializes reconnect/teardown per server. Concurrent tool-invocation
    // failures against the same server (see InvokeSharedAsync) can each
    // independently reach TryReconnectAsync; without this gate they would race
    // to tear down and rebuild the same McpClient, leaking the loser's
    // client/process. Also shared by the proactive-refresh sweep's teardown on
    // a terminal invalid_grant.
    private readonly ConcurrentDictionary<McpServerName, SemaphoreSlim> _reconnectGates = new();

    public McpClientManager(
        Dictionary<string, McpServerEntry> serverEntries,
        ToolRegistry toolRegistry,
        ToolConfig toolConfig,
        McpOAuthService oauthService,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider,
        ILogger<McpClientManager> logger,
        SessionConfig? sessionConfig = null)
    {
        _serverEntries = serverEntries;
        _toolRegistry = toolRegistry;
        _toolConfig = toolConfig;
        _oauthService = oauthService;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider;
        _logger = logger;
        _maxToolDescriptionChars = sessionConfig?.Tuning.MaxToolDescriptionChars ?? 0;
        _maxToolSchemaWarnChars = sessionConfig?.Tuning.MaxToolSchemaWarnChars ?? 0;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, entry) in _serverEntries)
        {
            var serverName = new McpServerName(name);
            if (!entry.Enabled)
            {
                _statuses[serverName] = new McpServerStatus(serverName, McpConnectionState.Disabled, 0, null);
                _logger.LogInformation("MCP server '{Name}' is disabled, skipping", name);
                continue;
            }

            await ConnectAsync(serverName, entry, cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, client) in _clients)
        {
            try
            {
                await client.DisposeAsync();
                _logger.LogInformation("MCP client '{Name}' shut down", name.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error shutting down MCP client '{Name}'", name.Value);
            }
        }

        _clients.Clear();
        _sharedToolFunctions.Clear();
    }

    public McpClient? GetClient(McpServerName serverName)
    {
        return _clients.GetValueOrDefault(serverName);
    }

    public IReadOnlyDictionary<McpServerName, McpServerStatus> GetServerStatuses() => _statuses;

    /// <summary>
    /// Returns discovered tool names for a connected MCP server.
    /// </summary>
    public IReadOnlyList<string> GetToolNames(McpServerName serverName)
    {
        if (!_sharedToolFunctions.TryGetValue(serverName, out var tools))
            return [];

        return tools.Keys.Order(StringComparer.Ordinal).ToList();
    }

    public async Task<bool> TryReconnectAsync(McpServerName serverName, CancellationToken ct = default)
    {
        if (!_serverEntries.TryGetValue(serverName.Value, out var entry) || !entry.Enabled)
            return false;

        var gate = _reconnectGates.GetOrAdd(serverName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_clients.TryRemove(serverName, out var existing))
            {
                try { await existing.DisposeAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Error disposing MCP client '{Name}' during reconnect", serverName.Value); }
            }

            _sharedToolFunctions.TryRemove(serverName, out _);

            return await ConnectAsync(serverName, entry, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Proactively refreshes OAuth tokens for connected, OAuth-managed servers
    /// ahead of expiry (see <see cref="McpOAuthService.ProactiveRefreshWindow"/>),
    /// and surfaces advance warnings for token sets that cannot self-heal. If a
    /// refresh comes back terminally rejected (invalid_grant), the connection
    /// is torn down immediately and marked with a status distinct from a
    /// generic connectivity failure or "never authenticated" — see
    /// <see cref="CreateRefreshRejectedStatus"/>. Called once per
    /// <see cref="McpReconnectionService"/> tick.
    /// </summary>
    public async Task RefreshOAuthTokensAsync(CancellationToken ct = default)
    {
        foreach (var (name, status) in _statuses)
        {
            if (status.State is not McpConnectionState.Connected)
                continue;

            if (!_serverEntries.TryGetValue(name.Value, out var entry) || !entry.Enabled)
                continue;

            if (_oauthService.GetTokenSet(name) is null)
                continue; // not an OAuth-managed server

            try
            {
                _oauthService.NoteUnknownExpiryOnce(name);
                _oauthService.WarnIfMissingRefreshToken(name);

                await _oauthService.GetValidTokenAsync(name, entry, ct);

                var tokenSet = _oauthService.GetTokenSet(name);
                if (tokenSet is null
                    && _oauthService.TryGetTerminalRefreshRejection(name, out var errorCode))
                {
                    // Tokens were just cleared: terminal rejection
                    // (invalid_grant / invalid_client / unauthorized_client —
                    // already logged and alerted by McpOAuthService). Tear the
                    // connection down now rather than waiting for the next
                    // tool call to 401 into a generic failure.
                    await TearDownConnectionAsync(name);
                    _statuses[name] = CreateRefreshRejectedStatus(name, errorCode);
                    _logger.LogWarning(
                        "MCP server '{Name}' OAuth refresh terminally rejected ({ErrorCode}); disconnected pending re-authorization",
                        name.Value, errorCode);
                    continue;
                }

                var advisory = BuildOAuthAdvisory(name, tokenSet);
                if (_statuses.TryGetValue(name, out var current) && current.ErrorMessage != advisory)
                    _statuses[name] = current with { ErrorMessage = advisory };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Proactive OAuth token refresh threw for MCP server '{Name}'", name.Value);
            }
        }
    }

    private async Task TearDownConnectionAsync(McpServerName name)
    {
        var gate = _reconnectGates.GetOrAdd(name, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_clients.TryRemove(name, out var client))
            {
                try { await client.DisposeAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Error disposing MCP client '{Name}' after auth revocation", name.Value); }
            }

            _sharedToolFunctions.TryRemove(name, out _);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Advisory text for an otherwise-healthy Connected status: null unless the
    /// token set is time-limited but has no refresh token, in which case the
    /// server will hard-fail at expiry with no chance of silent recovery.
    /// Surfaced through <c>McpServerStatusDto.Error</c> even for the Connected
    /// state so `netclaw doctor` and the daemon status API can show it ahead
    /// of time (see McpServersDoctorCheck's "Connected" case).
    /// </summary>
    private static string? BuildOAuthAdvisory(McpServerName serverName, McpOAuthTokenSet? tokenSet)
        => tokenSet is { RefreshToken: null, ExpiresAt: not null }
            ? $"No refresh token — re-authorization will be required at expiry. Run: netclaw mcp auth {serverName.Value}"
            : null;

    public async Task<string> InvokeAsync(
        string serverName,
        string toolName,
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        CancellationToken ct = default)
    {
        var server = new McpServerName(serverName);
        var tool = new ToolName(toolName);

        return await InvokeSharedAsync(server, tool, arguments, ct);
    }

    private async Task<string> InvokeSharedAsync(
        McpServerName serverName,
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        if (!TryGetSharedFunction(serverName, toolName.Value, out var function) || function is null)
        {
            var reconnected = await TryReconnectAsync(serverName, ct);
            if (!reconnected
                || !TryGetSharedFunction(serverName, toolName.Value, out function)
                || function is null)
            {
                throw new InvalidOperationException(
                    $"MCP server '{serverName.Value}' is unavailable or tool '{toolName.Value}' is not registered.");
            }
        }

        try
        {
            return await InvokeFunctionAsync(function, $"{serverName.Value}/{toolName.Value}", arguments, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "MCP tool '{ToolName}' failed on shared client '{ServerName}', attempting reconnect",
                toolName.Value, serverName.Value);

            var reconnected = await TryReconnectAsync(serverName, ct);
            if (!reconnected
                || !TryGetSharedFunction(serverName, toolName.Value, out var retryFunction)
                || retryFunction is null)
                throw;

            return await InvokeFunctionAsync(retryFunction, $"{serverName.Value}/{toolName.Value}", arguments, ct);
        }
    }

    // qualifiedToolName is the server-qualified "server/tool" name (not the bare
    // function.Name, which omits the server) so MCP error attribution matches the
    // bound-tool path (McpToolAdapter.Name) — otherwise the same error renders
    // differently depending on which invocation path produced it.
    private static async Task<string> InvokeFunctionAsync(
        AIFunction function,
        string qualifiedToolName,
        IDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        var aiArgs = arguments is { Count: > 0 }
            ? new AIFunctionArguments(arguments)
            : null;

        var result = await function.InvokeAsync(aiArgs, ct);
        // The SDK returns the whole CallToolResult as raw JSON on isError; surface
        // a clean, attributed error instead of a blob the model can't classify (#1495).
        return McpToolResultFormatter.Format(result, qualifiedToolName);
    }

    private bool TryGetSharedFunction(McpServerName serverName, string toolName, out AIFunction? function)
    {
        function = null;

        if (!_sharedToolFunctions.TryGetValue(serverName, out var serverTools))
            return false;

        return serverTools.TryGetValue(toolName, out function);
    }

    private async Task<bool> ConnectAsync(McpServerName name, McpServerEntry entry, CancellationToken ct)
    {
        // Holds the client until ownership passes to _clients. If the connect
        // fails after the client — and its child process — is created but
        // before that handoff (e.g. ListToolsAsync throws), the finally
        // disposes it so the process is not orphaned.
        McpClient? client = null;
        try
        {
            client = await CreateClientAsync(name, entry, ct, updateStatusOnAuthFailure: true);
            if (client is null)
                return false;

            var tools = await client.ListToolsAsync(cancellationToken: ct);
            var sharedFunctions = CreateFunctionMap(tools);
            LogToolDrift(name, tools);

            _toolRegistry.WithMcpTools(name.Value, tools, entry.GrantCategory, this,
                _maxToolDescriptionChars, _maxToolSchemaWarnChars, _logger);

            _sharedToolFunctions[name] = sharedFunctions;
            _clients[name] = client;
            client = null;

            // Advance warning for OAuth-managed servers whose token can never
            // self-refresh, surfaced both in the log and in the Connected
            // status text (doctor/status API) — see BuildOAuthAdvisory.
            var tokenSet = _oauthService.GetTokenSet(name);
            if (tokenSet is not null)
                _oauthService.WarnIfMissingRefreshToken(name);
            _statuses[name] = new McpServerStatus(name, McpConnectionState.Connected, tools.Count, BuildOAuthAdvisory(name, tokenSet));

            _logger.LogInformation("MCP server '{Name}' connected ({ToolCount} tools)", name.Value, tools.Count);
            return true;
        }
        catch (Exception ex)
        {
            if (_clients.TryRemove(name, out var existing))
            {
                try
                {
                    await existing.DisposeAsync();
                }
                catch (Exception disposeEx)
                {
                    _logger.LogDebug(disposeEx,
                        "Error disposing MCP client '{Name}' after failed connect rollback", name.Value);
                }
            }

            _sharedToolFunctions.TryRemove(name, out _);

            var hasCachedTokens = _oauthService.GetTokenSet(name) is not null;
            var hasOAuthRuntimeHints = HasOAuthRuntimeHints(name, entry);
            var failureStatus = BuildConnectionFailureStatus(name, entry, ex, hasCachedTokens, hasOAuthRuntimeHints);
            _statuses[name] = failureStatus;

            if (failureStatus.State is McpConnectionState.AwaitingAuth)
            {
                _logger.LogWarning(ex, "MCP server '{Name}' requires OAuth authorization", name.Value);
                EmitAuthAlert(name, $"MCP server '{name.Value}' requires OAuth authorization. Run: netclaw mcp auth {name.Value}", "authorization_required");
            }
            else if (failureStatus.State is McpConnectionState.AuthFailed)
            {
                _logger.LogWarning(ex, "MCP server '{Name}' authentication failed", name.Value);

                if (hasOAuthRuntimeHints || hasCachedTokens)
                {
                    EmitAuthAlert(name,
                        $"MCP server '{name.Value}' authentication failed. Run: netclaw mcp auth {name.Value}",
                        hasCachedTokens ? "token_rejected" : "credentials_rejected");
                }
                else
                {
                    EmitDisconnectedAlert(name, $"MCP server '{name.Value}' authentication failed: {failureStatus.ErrorMessage}");
                }
            }
            else
            {
                _logger.LogWarning(ex, "Failed to connect to MCP server '{Name}'", name.Value);
                EmitDisconnectedAlert(name, $"MCP server '{name.Value}' connection failed: {failureStatus.ErrorMessage}");
            }

            return false;
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch (Exception disposeEx)
                {
                    _logger.LogDebug(disposeEx,
                        "Error disposing MCP client '{Name}' after failed connect", name.Value);
                }
            }
        }
    }

    private async Task<McpClient?> CreateClientAsync(
        McpServerName name,
        McpServerEntry entry,
        CancellationToken ct,
        bool updateStatusOnAuthFailure)
    {
        // For HTTP transports without cached tokens, check if OAuth is needed
        // before attempting a connection that would fail with 401.
        if (entry.Transport is not "stdio" && updateStatusOnAuthFailure && entry.Url is not null)
        {
            var hasTokens = _oauthService.GetTokenSet(name) is not null;
            if (!hasTokens)
            {
                // Always probe so metadata is cached for the runtime fallback
                // in BuildConnectionFailureStatus. Only block the connection
                // when no static headers are configured — if the user supplied
                // headers, let the real connection attempt decide. See #1350.
                var metadata = await _oauthService.TryDiscoverMetadataAsync(name, entry.Url, ct);
                if (metadata is not null && entry.Headers is not { Count: > 0 })
                {
                    // A prior terminal refresh rejection cleared this server's
                    // tokens; keep the distinct "refresh rejected" status
                    // rather than downgrading to generic AwaitingAuth on every
                    // subsequent reconnect attempt. The terminal alert (with
                    // the actual error code) already fired at rejection time.
                    if (_oauthService.TryGetTerminalRefreshRejection(name, out var priorRejection))
                    {
                        _statuses[name] = CreateRefreshRejectedStatus(name, priorRejection);
                        return null;
                    }

                    _statuses[name] = CreateAwaitingAuthStatus(name);
                    _logger.LogWarning("MCP server '{Name}' requires OAuth authorization", name.Value);
                    EmitAuthAlert(name, $"MCP server '{name.Value}' requires OAuth authorization. Run: netclaw mcp auth {name.Value}", "authorization_required");

                    return null;
                }
            }
            else
            {
                // Route the (re)connect through Netclaw's logged, single-flighted
                // refresh path first, so the cached token the SDK's ITokenCache
                // bridge (McpTokenCacheAdapter) sees is already fresh. The SDK's
                // own on-401 refresh — silent, and headless-fatal on our stubbed
                // AuthorizationRedirectDelegate — then almost never has to run.
                await _oauthService.GetValidTokenAsync(name, entry, ct);

                if (_oauthService.GetTokenSet(name) is null
                    && _oauthService.TryGetTerminalRefreshRejection(name, out var errorCode))
                {
                    // The refresh above came back terminally rejected
                    // (invalid_grant / invalid_client / unauthorized_client)
                    // and already cleared the tokens, logging and alerting.
                    // Fail fast with a status distinct from a generic
                    // connectivity failure instead of letting the doomed
                    // connection attempt fall through to the SDK's silent
                    // headless-auth failure path.
                    _statuses[name] = CreateRefreshRejectedStatus(name, errorCode);
                    _logger.LogWarning(
                        "MCP server '{Name}' OAuth refresh terminally rejected ({ErrorCode}); re-authorization required",
                        name.Value, errorCode);
                    return null;
                }
            }
        }

        var transport = CreateTransport(name, entry);

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new()
            {
                Name = "netclaw",
                Title = "Netclaw",
                Version = BuildInfo.Version,
                WebsiteUrl = "https://netclaw.dev",
                Description = "Open-source autonomous operations agent built on Akka.NET",
            },
        }, cancellationToken: ct);
    }

    private IClientTransport CreateTransport(McpServerName serverName, McpServerEntry entry)
    {
        if (entry.Transport is "stdio")
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = entry.Command!,
                Arguments = entry.Arguments ?? [],
                EnvironmentVariables = entry.EnvironmentVariables.ToRawNullableValues(StringComparer.OrdinalIgnoreCase),
                Name = serverName.Value,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            });
        }

        // Unwrap SensitiveString here at the transport boundary so the SDK
        // sees the actual credential, not SensitiveString.ToString()'s
        // redacted sentinel.
        var headers = entry.Headers.ToRawValues(StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Identify Netclaw to the remote MCP server. The SDK's HttpClientTransport
        // builds its own HttpClient internally, so this header dictionary is the
        // only seam — DelegatingHandlers can't reach it. User-configured headers
        // win: if an operator already sets User-Agent or X-Netclaw-Component,
        // we leave them alone.
        if (!headers.ContainsKey("User-Agent"))
            headers["User-Agent"] = NetclawUserAgent.Value;
        if (!headers.ContainsKey(NetclawUserAgent.ComponentHeader))
            headers[NetclawUserAgent.ComponentHeader] = "mcp";

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(entry.Url!),
            Name = serverName.Value,
            AdditionalHeaders = headers,
            TransportMode = entry.Transport is "sse"
                ? HttpTransportMode.Sse
                : HttpTransportMode.AutoDetect,
            OAuth = BuildOAuthOptions(serverName, entry),
        });
    }

    private ClientOAuthOptions? BuildOAuthOptions(McpServerName serverName, McpServerEntry entry)
    {
        var metadata = _oauthService.GetCachedMetadata(serverName);

        // Only wire OAuth if server is known to need it (has metadata or static config)
        if (metadata is null && string.IsNullOrWhiteSpace(entry.OAuthClientId))
            return null;

        var serverNameCapture = serverName;
        return new ClientOAuthOptions
        {
            RedirectUri = new Uri("http://127.0.0.1:5199/api/mcp/oauth/callback"),
            ClientId = metadata?.ClientId ?? entry.OAuthClientId,
            Scopes = ParseScopes(entry.OAuthScope),
            TokenCache = _oauthService.CreateTokenCache(serverName),
            // Return null to suppress the SDK's default browser-open behavior;
            // Netclaw handles interactive auth via `netclaw mcp auth`.
            AuthorizationRedirectDelegate = static (_, _, _) => Task.FromResult<string?>(null),
            DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = "netclaw",
                ResponseDelegate = (response, _) =>
                {
                    _oauthService.UpdateMetadataClientId(serverNameCapture, response.ClientId);
                    return Task.CompletedTask;
                },
            },
        };
    }

    private bool HasOAuthRuntimeHints(McpServerName serverName, McpServerEntry entry)
        => !string.IsNullOrWhiteSpace(entry.OAuthClientId)
           || !string.IsNullOrWhiteSpace(entry.OAuthScope)
           || _oauthService.GetCachedMetadata(serverName) is not null;

    internal static McpServerStatus BuildConnectionFailureStatus(
        McpServerName serverName,
        McpServerEntry entry,
        Exception ex,
        bool hasCachedTokens,
        bool hasOAuthRuntimeHints)
    {
        if (IsAuthFailure(ex))
        {
            if (!hasCachedTokens && entry.Transport is not "stdio" && hasOAuthRuntimeHints)
                return CreateAwaitingAuthStatus(serverName);

            return CreateAuthFailedStatus(serverName, ex, oauthManaged: hasCachedTokens || hasOAuthRuntimeHints);
        }

        return CreateUnreachableStatus(serverName, ex);
    }

    internal static McpServerStatus CreateAwaitingAuthStatus(McpServerName serverName)
        => new(serverName, McpConnectionState.AwaitingAuth, 0,
            $"OAuth authorization required. Run: netclaw mcp auth {serverName.Value}");

    internal static McpServerStatus CreateAuthFailedStatus(McpServerName serverName, Exception ex, bool oauthManaged)
    {
        var statusText = GetHttpStatusText(ex);
        var detail = string.IsNullOrWhiteSpace(statusText)
            ? "Authentication rejected by server."
            : $"Authentication rejected by server ({statusText}).";
        var guidance = oauthManaged
            ? $" Run: netclaw mcp auth {serverName.Value}"
            : " Check configured credentials or headers.";
        return new(serverName, McpConnectionState.AuthFailed, 0, detail + guidance);
    }

    internal static McpServerStatus CreateUnreachableStatus(McpServerName serverName, Exception ex)
        => new(serverName, McpConnectionState.Unreachable, 0,
            string.IsNullOrWhiteSpace(ex.Message) ? "Failed to reach MCP server." : ex.Message);

    /// <summary>
    /// Distinct from <see cref="CreateAuthFailedStatus"/>'s generic "authentication
    /// rejected by server" text: this fires specifically when Netclaw's own
    /// refresh path (McpOAuthService) got a terminal token-endpoint rejection
    /// and cleared the stored tokens. <paramref name="errorCode"/> names the
    /// cause — invalid_grant (grant revoked by provider) vs invalid_client /
    /// unauthorized_client (client registration purged; re-auth performs a
    /// fresh registration) — so operators can tell either apart from "never
    /// authenticated" (AwaitingAuth) or a generic connectivity failure
    /// (Unreachable). Uses the AuthFailed state so the reconnection service's
    /// backoff loop — which only retries Unreachable servers — never
    /// auto-retries dead credentials.
    /// </summary>
    internal static McpServerStatus CreateRefreshRejectedStatus(McpServerName serverName, string errorCode)
        => new(serverName, McpConnectionState.AuthFailed, 0,
            $"Refresh rejected by provider ({errorCode}) — re-authorization required. Run: netclaw mcp auth {serverName.Value}");

    private void EmitAuthAlert(McpServerName serverName, string summary, string reason)
    {
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "mcp.auth.expired",
            AlertType.McpAuthExpired,
            summary,
            AlertSeverity.Warning,
            source: serverName.Value,
            context: new Dictionary<string, string>
            {
                ["serverName"] = serverName.Value,
                ["reason"] = reason,
            }));
    }

    private void EmitDisconnectedAlert(McpServerName serverName, string summary)
    {
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "mcp.server.disconnected",
            AlertType.McpServerDisconnected,
            summary,
            AlertSeverity.Warning,
            source: serverName.Value,
            context: new Dictionary<string, string> { ["serverName"] = serverName.Value }));
    }

    private static IEnumerable<string>? ParseScopes(string? scopeString)
    {
        if (string.IsNullOrWhiteSpace(scopeString))
            return null;

        return scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsAuthFailure(Exception ex)
    {
        // HttpRequestException with 401/403
        if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
            return true;

        if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check inner exceptions for auth failures
        if (ex.InnerException is not null)
            return IsAuthFailure(ex.InnerException);

        return false;
    }

    private static string? GetHttpStatusText(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: var statusCode } && statusCode is not null)
        {
            return statusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "401 Unauthorized",
                System.Net.HttpStatusCode.Forbidden => "403 Forbidden",
                _ => $"{(int)statusCode} {statusCode}"
            };
        }

        if (ex.InnerException is not null)
            return GetHttpStatusText(ex.InnerException);

        return null;
    }

    private static Dictionary<string, AIFunction> CreateFunctionMap(IList<McpClientTool> tools)
    {
        var map = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools)
            map[tool.Name] = tool;

        return map;
    }

    /// <summary>
    /// Compares discovered tools against <see cref="ToolAudienceProfile.McpServerToolGrants"/>
    /// across all audience profiles and logs warnings for drift.
    /// </summary>
    private void LogToolDrift(McpServerName serverName, IList<McpClientTool> discoveredTools)
    {
        var profiles = _toolConfig.AudienceProfiles;
        var allGrantedTools = new HashSet<string>(StringComparer.Ordinal);
        var hasAnyGrants = false;

        foreach (var profile in profiles.GetAllProfiles())
        {
            if (profile.McpServerToolGrants is not { } grants)
                continue;

            if (!grants.TryGetValue(serverName.Value, out var tools))
                continue;

            hasAnyGrants = true;
            foreach (var tool in tools)
                allGrantedTools.Add(tool);
        }

        if (!hasAnyGrants)
            return;

        var discoveredNames = new HashSet<string>(
            discoveredTools.Select(t => t.Name), StringComparer.Ordinal);

        var ungranted = discoveredNames.Except(allGrantedTools).ToList();
        var stale = allGrantedTools.Except(discoveredNames).ToList();

        if (ungranted.Count > 0)
        {
            _logger.LogWarning(
                "MCP server '{Name}' exposes {Count} tool(s) not granted to any audience: {Tools}. " +
                "Review and add to McpServerToolGrants if intended.",
                serverName.Value, ungranted.Count, string.Join(", ", ungranted));
        }

        if (stale.Count > 0)
        {
            _logger.LogWarning(
                "McpServerToolGrants for '{Name}' contains {Count} tool(s) not found on server: {Tools}. " +
                "These may have been removed or renamed.",
                serverName.Value, stale.Count, string.Join(", ", stale));
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            try { (client as IDisposable)?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing MCP client during shutdown"); }
        }

        _clients.Clear();
        _sharedToolFunctions.Clear();
    }
}

internal enum McpConnectionState
{
    Disabled,
    Connected,
    AwaitingAuth,
    AuthFailed,
    Unreachable,
}

internal sealed record McpServerStatus(
    McpServerName Name,
    McpConnectionState State,
    int ToolCount,
    string? ErrorMessage);
