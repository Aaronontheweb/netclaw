// -----------------------------------------------------------------------
// <copyright file="CrossProcessOAuthRefreshTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.RegularExpressions;
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

    public CrossProcessOAuthRefreshTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public async Task A_refresh_by_the_CLI_reaches_the_running_daemon()
    {
        var now = _time.GetUtcNow();
        OAuthTokenPersistence.PersistTokens(_paths, "codex",
            new OAuthDeviceFlowResult(new SensitiveString("access-0"), new SensitiveString("refresh-0"), now.AddMinutes(-1)),
            SecretsProtection.CreateProtector(_paths));

        // The token server rotates the refresh token: each one works once (RFC 6819 5.2.2.3).
        var current = "refresh-0";
        var generation = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var presented = Regex.Match(form, "refresh_token=([^&]+)").Groups[1].Value;
            if (presented != current)
                return FakeHttpMessageHandler.JsonResponse(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
            generation++;
            current = $"refresh-{generation}";
            return FakeHttpMessageHandler.JsonResponse(new
            {
                access_token = $"access-{generation}",
                refresh_token = current,
                expires_in = 3600,
            });
        });

        // The daemon bound its entry at startup, before the CLI refreshed.
        var daemonEntry = Entry("access-0", "refresh-0", now.AddMinutes(-1));
        var daemonService = CreateService(handler);

        var cliService = CreateService(handler);
        await cliService.GetValidAccessTokenAsync("codex",
            Entry("access-0", "refresh-0", now.AddMinutes(-1)), OpenAiOAuth, TestContext.Current.CancellationToken);

        var token = await daemonService.GetValidAccessTokenAsync("codex", daemonEntry, OpenAiOAuth,
            TestContext.Current.CancellationToken);

        Assert.Equal("access-1", token.Value);
        Assert.Equal(1, generation);
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
        return new ProviderOAuthTokenRefreshService(_paths, factory, null, _time);
    }

    public void Dispose() => _dir.Dispose();
}
