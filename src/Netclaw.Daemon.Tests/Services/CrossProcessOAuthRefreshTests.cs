// -----------------------------------------------------------------------
// <copyright file="CrossProcessOAuthRefreshTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

// `netclaw provider` probes each configured provider and refreshes an expired OAuth token. That
// refresh rotates the refresh token while the daemon runs. The daemon must spend the rotated token.
public sealed class CrossProcessOAuthRefreshTests : IDisposable
{
    private static readonly OAuthAuth OpenAiOAuth = new()
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice, AuthMethod.OAuthPkce],
        TokenEndpoint = new Uri("https://auth.openai.com/oauth/token"),
        DeviceEndpoint = new Uri("https://auth.openai.com/api/accounts/deviceauth/usercode"),
        ClientId = "client-id",
        UseProprietaryDeviceFlow = true,
    };

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly RotatingTokenServer _server = new();
    private readonly CapturingLogger _logger = new();

    public CrossProcessOAuthRefreshTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public async Task A_refresh_by_the_CLI_reaches_the_running_daemon()
    {
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var handler = new FakeHttpMessageHandler(_server.Handle);

        // The daemon bound its entry at startup, before the CLI refreshed.
        var daemonEntry = Entry("access-0", "refresh-0", now.AddMinutes(-1));
        var daemonService = CreateService(handler);

        var cliService = CreateService(handler);
        await cliService.GetValidAccessTokenAsync("codex",
            Entry("access-0", "refresh-0", now.AddMinutes(-1)), OpenAiOAuth, TestContext.Current.CancellationToken);

        var token = await daemonService.GetValidAccessTokenAsync("codex", daemonEntry, OpenAiOAuth,
            TestContext.Current.CancellationToken);

        Assert.Equal("access-1", token.Value);
        Assert.Equal(1, _server.Generation);
    }

    // The refresh succeeded at the token server, but the write to secrets.json failed. The disk now holds
    // the spent refresh token and the daemon holds the new one in memory.
    [Fact]
    public async Task A_failed_secrets_write_does_not_make_the_next_refresh_spend_the_old_token()
    {
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(SecretsWriteFailsAfterTheFirstRefresh(out var restoreSecrets));
        var entry = Entry("access-0", "refresh-0", now.AddMinutes(-1));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken));
        restoreSecrets();

        Assert.Equal("refresh-1", entry.OAuthRefreshToken!.Value);

        _time.Advance(TimeSpan.FromHours(2));
        var token = await service.GetValidAccessTokenAsync("codex", entry, OpenAiOAuth, TestContext.Current.CancellationToken);

        Assert.Equal("access-2", token.Value);
        Assert.Equal(["refresh-0", "refresh-0", "refresh-1"], _server.Presented);
        var recovered = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("in-memory", recovered.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_secrets_write_after_a_good_refresh_is_logged_as_an_error_with_the_path()
    {
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        var service = CreateService(SecretsWriteFailsAfterTheFirstRefresh(out _));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-0", now.AddMinutes(-1)), OpenAiOAuth,
                TestContext.Current.CancellationToken));

        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.NotNull(error.Exception);
        Assert.Contains(_paths.SecretsPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refresh_token_that_neither_the_disk_nor_memory_holds_asks_for_a_new_sign_in_after_two_attempts()
    {
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        _server.Reject("refresh-9");
        var service = CreateService(new FakeHttpMessageHandler(_server.Handle));

        await Assert.ThrowsAsync<ProviderOAuthRefreshRequiredException>(() =>
            service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-1", now.AddMinutes(-1)), OpenAiOAuth,
                TestContext.Current.CancellationToken));

        Assert.Equal(["refresh-0", "refresh-1"], _server.Presented);
    }

    [Fact]
    public async Task A_rejected_refresh_token_that_the_disk_and_memory_share_is_not_retried()
    {
        var now = _time.GetUtcNow();
        StoreTokens("access-0", "refresh-0", now.AddMinutes(-1));
        _server.Reject("refresh-9");
        var service = CreateService(new FakeHttpMessageHandler(_server.Handle));

        await Assert.ThrowsAsync<ProviderOAuthRefreshRequiredException>(() =>
            service.GetValidAccessTokenAsync("codex", Entry("access-0", "refresh-0", now.AddMinutes(-1)), OpenAiOAuth,
                TestContext.Current.CancellationToken));

        Assert.Equal(["refresh-0"], _server.Presented);
    }

    private void StoreTokens(string access, string refresh, DateTimeOffset expiry)
        => OAuthTokenPersistence.PersistTokens(_paths, "codex",
            new OAuthDeviceFlowResult(new SensitiveString(access), new SensitiveString(refresh), expiry),
            SecretsProtection.CreateProtector(_paths));

    // After the token server answers, a directory takes the place of secrets.json, so the save that
    // follows fails on every platform. The returned action puts the old file back, as it was on disk.
    private FakeHttpMessageHandler SecretsWriteFailsAfterTheFirstRefresh(out Action restoreSecrets)
    {
        var secretsBeforeRefresh = File.ReadAllBytes(_paths.SecretsPath);
        var broken = false;
        restoreSecrets = () =>
        {
            Directory.Delete(_paths.SecretsPath);
            File.WriteAllBytes(_paths.SecretsPath, secretsBeforeRefresh);
        };

        return new FakeHttpMessageHandler(request =>
        {
            var response = _server.Handle(request);
            if (!broken)
            {
                broken = true;
                File.Delete(_paths.SecretsPath);
                Directory.CreateDirectory(_paths.SecretsPath);
            }

            return response;
        });
    }

    private static ProviderEntry Entry(string access, string refresh, DateTimeOffset? expiry) => new()
    {
        Type = "openai",
        AuthMethod = AuthMethod.OAuthDevice,
        OAuthAccessToken = new SensitiveString(access),
        OAuthRefreshToken = new SensitiveString(refresh),
        OAuthTokenExpiry = expiry,
        OAuthAccountId = new SensitiveString("account"),
    };

    private ProviderOAuthTokenRefreshService CreateService(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var factory = new DeviceFlowServiceFactory(new OAuthDeviceFlowService(http, _time), new OpenAiDeviceFlowService(http, _time));
        return new ProviderOAuthTokenRefreshService(_paths, factory, _logger, null, _time);
    }

    public void Dispose() => _dir.Dispose();

    // The token server rotates the refresh token: each one works once (RFC 6819 5.2.2.3).
    private sealed class RotatingTokenServer
    {
        private readonly object _gate = new();
        private string _current = "refresh-0";

        public int Generation { get; private set; }

        public List<string> Presented { get; } = [];

        public void Reject(string current)
        {
            lock (_gate)
                _current = current;
        }

        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var presented = Regex.Match(form, "refresh_token=([^&]+)").Groups[1].Value;
            lock (_gate)
            {
                Presented.Add(presented);
                if (presented != _current)
                    return FakeHttpMessageHandler.JsonResponse(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);

                Generation++;
                _current = $"refresh-{Generation}";
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    access_token = $"access-{Generation}",
                    refresh_token = _current,
                    expires_in = 3600,
                });
            }
        }
    }

    private sealed class CapturingLogger : ILogger<ProviderOAuthTokenRefreshService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
