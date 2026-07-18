// -----------------------------------------------------------------------
// <copyright file="McpOAuthServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpOAuthServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    // ── Refresh: single-flight, invalid_grant, transient, proactive, missing-refresh-token ──

    [Fact]
    public async Task GetValidTokenAsync_InvalidGrant_ClearsTokensAndDoesNotRetry()
    {
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var sink = new RecordingNotificationSink();
        var handler = new TokenEndpointHandler
        {
            RefreshResponse = _ => FakeHttpMessageHandler.JsonResponse(new { error = "invalid_grant" }, HttpStatusCode.BadRequest),
        };
        var serverName = new McpServerName("notion");
        var entry = CreateHttpEntry();
        var service = CreateService(CreateDiscoveryClient(), new OAuthPkceService(new HttpClient(handler), time),
            timeProvider: time, notificationSink: sink);

        await SeedTokenAsync(service, serverName, entry, handler, expiresInSeconds: 60, refreshToken: "refresh-old");
        time.Advance(TimeSpan.FromMinutes(5)); // well past expiry

        var token = await service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);

        Assert.Null(token);
        Assert.Null(service.GetTokenSet(serverName));
        Assert.Equal(1, handler.RefreshCallCount);

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(AlertType.McpAuthExpired, alert.Category);
        Assert.Equal("invalid_grant", alert.Context!["reason"]);

        // invalid_grant kills the grant, not the client registration — the
        // cached client_id must survive for the re-auth flow.
        Assert.Equal("test-client", service.GetCachedMetadata(serverName)!.ClientId);

        // Subsequent calls must not re-attempt refresh against cleared tokens.
        var second = await service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);
        Assert.Null(second);
        Assert.Equal(1, handler.RefreshCallCount);
    }

    [Fact]
    public async Task GetValidTokenAsync_InvalidClient_ClearsTokensAndClientRegistrationAndDoesNotRetry()
    {
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var sink = new RecordingNotificationSink();
        var handler = new TokenEndpointHandler
        {
            // invalid_client MAY be a 401 per RFC 6749 §5.2.
            RefreshResponse = _ => FakeHttpMessageHandler.JsonResponse(new { error = "invalid_client" }, HttpStatusCode.Unauthorized),
        };
        var serverName = new McpServerName("notion");
        var entry = CreateHttpEntry();
        var service = CreateService(CreateDiscoveryClient(), new OAuthPkceService(new HttpClient(handler), time),
            timeProvider: time, notificationSink: sink);

        await SeedTokenAsync(service, serverName, entry, handler, expiresInSeconds: 60, refreshToken: "refresh-old");
        Assert.Equal("test-client", service.GetCachedMetadata(serverName)!.ClientId);
        time.Advance(TimeSpan.FromMinutes(5)); // well past expiry

        var token = await service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);

        // Terminal like invalid_grant: tokens cleared, alert with the actual code.
        Assert.Null(token);
        Assert.Null(service.GetTokenSet(serverName));
        Assert.Equal(1, handler.RefreshCallCount);
        Assert.True(service.TryGetTerminalRefreshRejection(serverName, out var errorCode));
        Assert.Equal("invalid_client", errorCode);

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(AlertType.McpAuthExpired, alert.Category);
        Assert.Equal("invalid_client", alert.Context!["reason"]);

        // The client registration itself is dead — the cached client_id must be
        // dropped so the next `netclaw mcp auth` re-registers instead of
        // reusing it (EnsureClientRegisteredAsync short-circuits on it).
        Assert.Null(service.GetCachedMetadata(serverName)!.ClientId);

        // No retry loop against dead credentials.
        var second = await service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);
        Assert.Null(second);
        Assert.Equal(1, handler.RefreshCallCount);
    }

    [Fact]
    public async Task GetValidTokenAsync_InvalidClient_ReauthPerformsFreshClientRegistration()
    {
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var registrationCount = 0;
        // DCR-capable auth server, and a server entry with NO static client id,
        // so the client_id in play is dynamically registered — the incident
        // scenario where the provider purges DCR'd client IDs.
        var discovery = new HttpClient(new FakeHttpMessageHandler(request => request.RequestUri!.ToString() switch
        {
            "https://mcp.example.com/" or "https://mcp.example.com" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "https://mcp.example.com/.well-known/oauth-protected-resource" => JsonResponse(new
            {
                authorization_servers = new[] { "https://auth.example.com" },
                resource = "https://mcp.example.com/resource"
            }),
            "https://auth.example.com/.well-known/oauth-authorization-server" => JsonResponse(new
            {
                authorization_endpoint = "https://auth.example.com/authorize",
                token_endpoint = "https://auth.example.com/token",
                registration_endpoint = "https://auth.example.com/register"
            }),
            "https://auth.example.com/register" => JsonResponse(new { client_id = $"dcr-client-{++registrationCount}" }),
            _ => throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}")
        }));
        var handler = new TokenEndpointHandler
        {
            RefreshResponse = _ => FakeHttpMessageHandler.JsonResponse(new { error = "invalid_client" }, HttpStatusCode.Unauthorized),
        };
        var serverName = new McpServerName("notion");
        var entry = new McpServerEntry { Transport = "http", Url = "https://mcp.example.com" };
        var service = CreateService(discovery, new OAuthPkceService(new HttpClient(handler), time), timeProvider: time);

        await SeedTokenAsync(service, serverName, entry, handler, expiresInSeconds: 60, refreshToken: "refresh-old");
        Assert.Equal(1, registrationCount);
        Assert.Equal("dcr-client-1", service.GetCachedMetadata(serverName)!.ClientId);

        time.Advance(TimeSpan.FromMinutes(5));
        await service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);
        Assert.Null(service.GetTokenSet(serverName));

        // Re-auth must perform a FRESH dynamic registration, not reuse the dead
        // client_id, and a completed flow clears the terminal-rejection record.
        var (_, state) = await service.StartAuthorizationFlowAsync(serverName, entry, TestContext.Current.CancellationToken);
        Assert.Equal(2, registrationCount);
        Assert.Equal("dcr-client-2", service.GetCachedMetadata(serverName)!.ClientId);

        await service.CompleteAuthorizationAsync("code-2", state, TestContext.Current.CancellationToken);
        Assert.NotNull(service.GetTokenSet(serverName));
        Assert.False(service.TryGetTerminalRefreshRejection(serverName, out _));
    }

    [Fact]
    public async Task GetValidTokenAsync_TransientFailure_RetainsTokensAndLogsStatus()
    {
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var handler = new TokenEndpointHandler
        {
            RefreshResponse = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        };
        var serverName = new McpServerName("notion");
        var entry = CreateHttpEntry();
        var logger = new RecordingLogger<McpOAuthService>();
        var service = CreateService(CreateDiscoveryClient(), new OAuthPkceService(new HttpClient(handler), time),
            timeProvider: time, logger: logger);

        await SeedTokenAsync(service, serverName, entry, handler, expiresInSeconds: 60, refreshToken: "refresh-old");
        time.Advance(TimeSpan.FromMinutes(5));

        var token = await service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);

        Assert.Null(token);
        Assert.Equal(1, handler.RefreshCallCount);

        // Tokens retained — server stays retryable, not clobbered by a transient 503.
        var retained = service.GetTokenSet(serverName);
        Assert.NotNull(retained);
        Assert.Equal("refresh-old", retained!.RefreshToken!.Value);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("notion", StringComparison.Ordinal)
            && e.Message.Contains("ServiceUnavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetValidTokenAsync_ConcurrentCallsWithExpiredToken_SingleFlightsRefresh()
    {
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var handler = new GatedTokenEndpointHandler();
        var serverName = new McpServerName("notion");
        var entry = CreateHttpEntry();
        var service = CreateService(CreateDiscoveryClient(), new OAuthPkceService(new HttpClient(handler), time),
            timeProvider: time);

        await SeedTokenAsync(service, serverName, entry, handler, expiresInSeconds: 60, refreshToken: "refresh-old");
        time.Advance(TimeSpan.FromMinutes(5)); // well past expiry

        handler.RefreshResponseBody = new { access_token = "access-new", refresh_token = "refresh-new", expires_in = 3600 };

        var callTasks = new Task<string?>[5];
        for (var i = 0; i < callTasks.Length; i++)
            callTasks[i] = service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);

        await handler.WaitForFirstRefreshRequestAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        handler.ReleaseFirstRefreshRequest();

        var results = await Task.WhenAll(callTasks);

        Assert.Equal(1, handler.RefreshCallCount);
        Assert.All(results, r => Assert.Equal("access-new", r));

        var persisted = service.GetTokenSet(serverName);
        Assert.NotNull(persisted);
        Assert.Equal("refresh-new", persisted!.RefreshToken!.Value);
    }

    [Fact]
    public async Task GetValidTokenAsync_WithinProactiveWindow_RefreshesAheadOfExpiry()
    {
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var handler = new TokenEndpointHandler
        {
            RefreshResponse = _ => FakeHttpMessageHandler.JsonResponse(new
            {
                access_token = "access-new",
                refresh_token = "refresh-new",
                expires_in = 3600,
            }),
        };
        var serverName = new McpServerName("notion");
        var entry = CreateHttpEntry();
        var service = CreateService(CreateDiscoveryClient(), new OAuthPkceService(new HttpClient(handler), time),
            timeProvider: time);

        // Token is not yet expired, but its 15-minute remaining lifetime is
        // inside the 10-minute ProactiveRefreshWindow.
        await SeedTokenAsync(service, serverName, entry, handler, expiresInSeconds: (int)TimeSpan.FromMinutes(15).TotalSeconds, refreshToken: "refresh-old");
        time.Advance(TimeSpan.FromMinutes(6)); // 9 minutes remain — inside the window

        var token = await service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);

        Assert.Equal("access-new", token);
        Assert.Equal(1, handler.RefreshCallCount);
    }

    [Fact]
    public async Task GetValidTokenAsync_WellOutsideProactiveWindow_DoesNotRefresh()
    {
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var handler = new TokenEndpointHandler
        {
            RefreshResponse = _ => throw new InvalidOperationException("refresh should not be called"),
        };
        var serverName = new McpServerName("notion");
        var entry = CreateHttpEntry();
        var service = CreateService(CreateDiscoveryClient(), new OAuthPkceService(new HttpClient(handler), time),
            timeProvider: time);

        await SeedTokenAsync(service, serverName, entry, handler, expiresInSeconds: (int)TimeSpan.FromMinutes(30).TotalSeconds, refreshToken: "refresh-old");
        // 30 minutes remain — well outside the 10-minute proactive window.

        var token = await service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);

        Assert.Equal("access-seed", token);
        Assert.Equal(0, handler.RefreshCallCount);
    }

    [Fact]
    public async Task GetValidTokenAsync_MissingRefreshToken_WarnsAndDoesNotAttemptRefresh()
    {
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var sink = new RecordingNotificationSink();
        var handler = new TokenEndpointHandler
        {
            RefreshResponse = _ => throw new InvalidOperationException("refresh should not be called — no refresh token"),
        };
        var serverName = new McpServerName("notion");
        var entry = CreateHttpEntry();
        var service = CreateService(CreateDiscoveryClient(), new OAuthPkceService(new HttpClient(handler), time),
            timeProvider: time, notificationSink: sink);

        // Seed a token set with no refresh token at all (matches defect #4:
        // a configured OAuth server whose persisted token set never got one).
        await SeedTokenAsync(service, serverName, entry, handler, expiresInSeconds: 60, refreshToken: null);
        time.Advance(TimeSpan.FromMinutes(5)); // past expiry

        var token = await service.GetValidTokenAsync(serverName, entry, TestContext.Current.CancellationToken);

        Assert.Null(token);
        Assert.Equal(0, handler.RefreshCallCount);

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(AlertType.McpAuthExpired, alert.Category);
        Assert.Equal("no_refresh_token", alert.Context!["reason"]);
    }

    [Fact]
    public async Task WarnIfMissingRefreshToken_TokenSetWithoutRefreshToken_WarnsImmediatelyRegardlessOfExpiryWindow()
    {
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var sink = new RecordingNotificationSink();
        var service = CreateService(CreateDiscoveryClient(),
            CreatePkceService(JsonResponse(new { access_token = "access-tok", expires_in = 3600 })),
            timeProvider: time, notificationSink: sink);
        var serverName = new McpServerName("notion");
        var entry = CreateHttpEntry();

        // Far from expiry (1 hour, well outside the 10-minute window) but no
        // refresh token was ever issued — the operator should be warned now,
        // not left to discover it only once the token has already expired.
        var (_, state) = await service.StartAuthorizationFlowAsync(serverName, entry, TestContext.Current.CancellationToken);
        await service.CompleteAuthorizationAsync("code", state, TestContext.Current.CancellationToken);

        service.WarnIfMissingRefreshToken(serverName);

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal("no_refresh_token", alert.Context!["reason"]);

        // Calling again should not re-emit — de-duped until the condition changes.
        service.WarnIfMissingRefreshToken(serverName);
        Assert.Single(sink.Alerts);
    }

    [Fact]
    public async Task GetFlowStatusByState_ReauthWithExistingToken_RemainsPending()
    {
        var service = CreateService(
            CreateDiscoveryClient(),
            CreatePkceService(JsonResponse(new
            {
                access_token = "access-token",
                refresh_token = "refresh-token",
                expires_in = 3600
            })));

        var entry = CreateHttpEntry();

        var (_, initialState) = await service.StartAuthorizationFlowAsync(new McpServerName("textforge"), entry, CancellationToken.None);
        await service.CompleteAuthorizationAsync("first-code", initialState, CancellationToken.None);

        var (_, reauthState) = await service.StartAuthorizationFlowAsync(new McpServerName("textforge"), entry, CancellationToken.None);

        Assert.Equal(McpOAuthFlowStatus.Pending, service.GetFlowStatusByState(reauthState));
        Assert.Equal(McpOAuthFlowStatus.Pending, service.GetFlowStatus(new McpServerName("textforge")));
    }

    [Fact]
    public async Task GetFlowStatusByState_WhenTokenExchangeFails_ReturnsFailed()
    {
        var service = CreateService(
            CreateDiscoveryClient(),
            CreatePkceService(JsonResponse(new { error = "invalid_request" }, HttpStatusCode.BadRequest)));

        var (_, state) = await service.StartAuthorizationFlowAsync(new McpServerName("textforge"), CreateHttpEntry(), CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.CompleteAuthorizationAsync("bad-code", state, CancellationToken.None));

        Assert.Equal(McpOAuthFlowStatus.Failed, service.GetFlowStatusByState(state));
    }

    [Fact]
    public async Task LoadTokensFromDisk_survives_encrypted_round_trip()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);
        SensitiveStringTypeConverter.Protector = protector;

        try
        {
            // First service: complete auth flow → persist tokens (encrypted)
            var service1 = CreateService(CreateDiscoveryClient(),
                CreatePkceService(JsonResponse(new
                {
                    access_token = "access-tok",
                    refresh_token = "refresh-tok",
                    expires_in = 3600
                })),
                protector);

            var entry = CreateHttpEntry();
            var (_, state) = await service1.StartAuthorizationFlowAsync(new McpServerName("notion"), entry, CancellationToken.None);
            await service1.CompleteAuthorizationAsync("auth-code", state, CancellationToken.None);

            // Verify tokens were encrypted on disk
            var onDisk = File.ReadAllText(paths.SecretsPath);
            Assert.Contains("ENC:", onDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("access-tok", onDisk, StringComparison.Ordinal);

            // Second service: simulates daemon restart — must load encrypted tokens
            var service2 = CreateService(CreateDiscoveryClient(),
                CreatePkceService(JsonResponse(new { access_token = "unused" })),
                protector);

            var tokenSet = service2.GetTokenSet(new McpServerName("notion"));
            Assert.NotNull(tokenSet);
            Assert.Equal("access-tok", tokenSet.AccessToken.Value);
            Assert.NotNull(tokenSet.RefreshToken);
            Assert.Equal("refresh-tok", tokenSet.RefreshToken.Value);
            Assert.NotNull(tokenSet.ExpiresAt);
            Assert.True(tokenSet.ExpiresAt > DateTimeOffset.UtcNow);
        }
        finally
        {
            SensitiveStringTypeConverter.Protector = null;
        }
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    private McpOAuthService CreateService(HttpClient discoveryClient, OAuthPkceService pkceService,
        ISecretsProtector? protector = null,
        TimeProvider? timeProvider = null,
        IOperationalNotificationSink? notificationSink = null,
        ILogger<McpOAuthService>? logger = null)
    {
        return new McpOAuthService(
            discoveryClient,
            new NetclawPaths(_dir.Path),
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<McpOAuthService>.Instance,
            pkceService,
            notificationSink ?? NullNotificationSink.Instance,
            protector);
    }

    /// <summary>
    /// Completes an OAuth authorization flow through <paramref name="handler"/>'s
    /// exchange route so the service ends up with a real, persisted token set —
    /// the only way to populate <c>McpOAuthService</c>'s private token/metadata
    /// caches from outside the class.
    /// </summary>
    private static async Task SeedTokenAsync(
        McpOAuthService service,
        McpServerName serverName,
        McpServerEntry entry,
        ITokenEndpointHandler handler,
        int expiresInSeconds,
        string? refreshToken)
    {
        handler.ExchangeResponse = _ => refreshToken is null
            ? FakeHttpMessageHandler.JsonResponse(new { access_token = "access-seed", expires_in = expiresInSeconds })
            : FakeHttpMessageHandler.JsonResponse(new { access_token = "access-seed", refresh_token = refreshToken, expires_in = expiresInSeconds });

        var (_, state) = await service.StartAuthorizationFlowAsync(serverName, entry, CancellationToken.None);
        await service.CompleteAuthorizationAsync("seed-code", state, CancellationToken.None);
    }

    private interface ITokenEndpointHandler
    {
        Func<HttpRequestMessage, HttpResponseMessage>? ExchangeResponse { get; set; }
    }

    /// <summary>
    /// Fake token endpoint that routes on <c>grant_type</c> so the same handler
    /// can serve both the authorization-code exchange (to seed a token set) and
    /// a separately-scriptable refresh response, mirroring a real OAuth server
    /// backing a single token endpoint URL.
    /// </summary>
    private sealed class TokenEndpointHandler : HttpMessageHandler, ITokenEndpointHandler
    {
        private int _refreshCallCount;

        public Func<HttpRequestMessage, HttpResponseMessage>? ExchangeResponse { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? RefreshResponse { get; set; }

        public int RefreshCallCount => Volatile.Read(ref _refreshCallCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (!body.Contains("grant_type=refresh_token", StringComparison.Ordinal))
                return ExchangeResponse!(request);

            Interlocked.Increment(ref _refreshCallCount);
            return RefreshResponse!(request);
        }
    }

    /// <summary>
    /// Like <see cref="TokenEndpointHandler"/>, but the first refresh request
    /// blocks until the test releases it — used to prove single-flight
    /// behavior by forcing genuine overlap between concurrent
    /// <c>GetValidTokenAsync</c> callers instead of relying on incidental
    /// scheduling.
    /// </summary>
    private sealed class GatedTokenEndpointHandler : HttpMessageHandler, ITokenEndpointHandler
    {
        private readonly TaskCompletionSource _firstRefreshReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstRefresh =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _refreshCallCount;

        public Func<HttpRequestMessage, HttpResponseMessage>? ExchangeResponse { get; set; }
        public object RefreshResponseBody { get; set; } = new { access_token = "access-new", expires_in = 3600 };

        public int RefreshCallCount => Volatile.Read(ref _refreshCallCount);

        public Task WaitForFirstRefreshRequestAsync() => _firstRefreshReceived.Task;
        public void ReleaseFirstRefreshRequest() => _releaseFirstRefresh.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (!body.Contains("grant_type=refresh_token", StringComparison.Ordinal))
                return ExchangeResponse!(request);

            if (Interlocked.Increment(ref _refreshCallCount) == 1)
            {
                _firstRefreshReceived.TrySetResult();
                await _releaseFirstRefresh.Task.WaitAsync(cancellationToken);
            }

            return FakeHttpMessageHandler.JsonResponse(RefreshResponseBody);
        }
    }

    private sealed class RecordingNotificationSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];
        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static McpServerEntry CreateHttpEntry()
    {
        return new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            OAuthClientId = "test-client"
        };
    }

    private static HttpClient CreateDiscoveryClient()
    {
        return new HttpClient(new FakeHttpMessageHandler(request => request.RequestUri!.ToString() switch
        {
            "https://mcp.example.com/" or "https://mcp.example.com" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "https://mcp.example.com/.well-known/oauth-protected-resource" => JsonResponse(new
            {
                authorization_servers = new[] { "https://auth.example.com" },
                resource = "https://mcp.example.com/resource"
            }),
            "https://auth.example.com/.well-known/oauth-authorization-server" => JsonResponse(new
            {
                authorization_endpoint = "https://auth.example.com/authorize",
                token_endpoint = "https://auth.example.com/token"
            }),
            _ => throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}")
        }));
    }

    private static OAuthPkceService CreatePkceService(HttpResponseMessage tokenResponse)
    {
        return new OAuthPkceService(new HttpClient(new FakeHttpMessageHandler(request => request.RequestUri!.ToString() switch
        {
            "https://auth.example.com/token" => tokenResponse,
            _ => throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}")
        })));
    }

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

}
