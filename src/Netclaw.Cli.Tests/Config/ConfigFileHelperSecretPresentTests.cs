// -----------------------------------------------------------------------
// <copyright file="ConfigFileHelperSecretPresentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

public sealed class ConfigFileHelperSecretPresentTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ConfigFileHelperSecretPresentTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void SecretPresent_NoFile_ReturnsFalse()
    {
        Assert.False(ConfigFileHelper.SecretPresent(_paths, "Slack.BotToken"));
    }

    [Fact]
    public void SecretPresent_PathMissing_ReturnsFalse()
    {
        File.WriteAllText(_paths.SecretsPath, """{"Slack": {"AppToken": "ENC:xyz"}}""");
        Assert.False(ConfigFileHelper.SecretPresent(_paths, "Slack.BotToken"));
        Assert.False(ConfigFileHelper.SecretPresent(_paths, "Discord.BotToken"));
    }

    [Fact]
    public void SecretPresent_EncryptedString_ReturnsTrue_WithoutDecryption()
    {
        // ENC: prefix denotes encrypted value; the probe must not decrypt.
        File.WriteAllText(_paths.SecretsPath, """{"Slack": {"BotToken": "ENC:abcdef"}}""");
        Assert.True(ConfigFileHelper.SecretPresent(_paths, "Slack.BotToken"));
    }

    [Fact]
    public void SecretPresent_PlaintextString_ReturnsTrue()
    {
        File.WriteAllText(_paths.SecretsPath, """{"Slack": {"BotToken": "raw-token"}}""");
        Assert.True(ConfigFileHelper.SecretPresent(_paths, "Slack.BotToken"));
    }

    [Fact]
    public void SecretPresent_EmptyString_ReturnsFalse()
    {
        File.WriteAllText(_paths.SecretsPath, """{"Slack": {"BotToken": ""}}""");
        Assert.False(ConfigFileHelper.SecretPresent(_paths, "Slack.BotToken"));
    }

    [Fact]
    public void SecretPresent_JsonNull_ReturnsFalse()
    {
        File.WriteAllText(_paths.SecretsPath, """{"Slack": {"BotToken": null}}""");
        Assert.False(ConfigFileHelper.SecretPresent(_paths, "Slack.BotToken"));
    }

    [Fact]
    public void SecretPresent_NestedPath_ResolvesCorrectly()
    {
        File.WriteAllText(_paths.SecretsPath,
            """{"Providers": {"openai": {"ApiKey": "ENC:secret"}}}""");

        Assert.True(ConfigFileHelper.SecretPresent(_paths, "Providers.openai.ApiKey"));
        Assert.False(ConfigFileHelper.SecretPresent(_paths, "Providers.openai.Endpoint"));
    }

    [Fact]
    public void SecretPresent_InvalidJson_ReturnsFalse()
    {
        File.WriteAllText(_paths.SecretsPath, "{ broken");
        Assert.False(ConfigFileHelper.SecretPresent(_paths, "Slack.BotToken"));
    }

    [Fact]
    public void SecretPresent_NullPath_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException
        // for null (which is an ArgumentException) and ArgumentException for blank.
        Assert.ThrowsAny<ArgumentException>(() =>
            ConfigFileHelper.SecretPresent(_paths, null!));
        Assert.Throws<ArgumentException>(() =>
            ConfigFileHelper.SecretPresent(_paths, "  "));
    }
}
