// -----------------------------------------------------------------------
// <copyright file="OAuthTokenPersistenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public sealed class OAuthTokenPersistenceTests
{
    [Fact]
    public void PersistTokens_RemovesStaleOptionalOAuthFieldsWhenResultOmitsThem()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        OAuthTokenPersistence.PersistTokens(
            paths,
            "openai",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-1"),
                new SensitiveString("refresh-1"),
                DateTimeOffset.Parse("2026-06-01T00:00:00+00:00"),
                new SensitiveString("account-1")));

        OAuthTokenPersistence.PersistTokens(
            paths,
            "openai",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-2"),
                null,
                null));

        using var doc = JsonDocument.Parse(File.ReadAllText(paths.SecretsPath));
        var provider = doc.RootElement
            .GetProperty("Providers")
            .GetProperty("openai");

        Assert.Equal("access-2", provider.GetProperty("OAuthAccessToken").GetString());
        Assert.False(provider.TryGetProperty("OAuthRefreshToken", out _));
        Assert.False(provider.TryGetProperty("OAuthAccountId", out _));

        using var configDoc = JsonDocument.Parse(File.ReadAllText(paths.NetclawConfigPath));
        var configProvider = configDoc.RootElement
            .GetProperty("Providers")
            .GetProperty("openai");

        Assert.False(configProvider.TryGetProperty("OAuthTokenExpiry", out _));
    }
}
