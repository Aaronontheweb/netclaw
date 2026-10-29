// -----------------------------------------------------------------------
// <copyright file="ConfigReloadBaselineTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Services;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigReloadBaselineTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "present", true)]
    [InlineData("loaded", null, false)]
    [InlineData("same", "same", false)]
    [InlineData("loaded", "current", true)]
    public void Startup_reload_requires_a_present_file_with_a_different_hash(
        string? loaded, string? current, bool expected)
        => Assert.Equal(expected, ConfigWatcherActor.NeedsStartupReload(loaded, current));

    [Fact]
    public void The_hash_and_configuration_use_the_same_captured_bytes()
    {
        const string loaded = "/* comment */ { \"Daemon\": { \"Host\": \"loaded\", }, }";
        const string current = """{ "Daemon": { "Host": "current" } }""";
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, loaded);
        var bytes = ConfigReloadBaseline.ReadNetclawJson(paths);
        File.WriteAllText(paths.NetclawConfigPath, current);
        using var configuration = new ConfigurationManager();

        var baseline = ConfigReloadBaseline.Load(configuration, paths, bytes);

        Assert.Equal("loaded", configuration["Daemon:Host"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loaded))), baseline.NetclawJsonHash);
        Assert.True(ConfigWatcherActor.NeedsStartupReload(
            baseline.NetclawJsonHash, ConfigReloadBaseline.HashNetclawJson(ConfigReloadBaseline.ReadNetclawJson(paths))));
    }

    [Fact]
    public void Source_precedence_and_the_lower_priority_snapshot_remain_distinct()
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.SecretsPath, """{ "ReloadTest": { "Secret": "overlay", "Environment": "overlay" } }""");
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReloadTest:Base"] = "lower", ["ReloadTest:Secret"] = "lower", ["ReloadTest:OnlyLower"] = "lower"
        });
        const string variable = "NETCLAW_ReloadTest__Environment";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "environment");
        try
        {
            var bytes = Encoding.UTF8.GetBytes("""{ "ReloadTest": { "Base": "base", "Secret": "base", "Environment": "base" } }""");
            var baseline = ConfigReloadBaseline.Load(configuration, paths, bytes);

            Assert.Equal("base", configuration["ReloadTest:Base"]);
            Assert.Equal("overlay", configuration["ReloadTest:Secret"]);
            Assert.Equal("environment", configuration["ReloadTest:Environment"]);
            Assert.Equal("lower", configuration["ReloadTest:OnlyLower"]);
            Assert.Equal("lower", baseline.LowerPriorityConfiguration["ReloadTest:Base"]);
            Assert.False(baseline.LowerPriorityConfiguration.ContainsKey("ReloadTest:Environment"));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Theory]
    [InlineData("{ broken json", null, "netclaw.json")]
    [InlineData("{}", "{ broken json", "secrets.json")]
    public void Canonical_load_errors_keep_the_source_identity(string config, string? secrets, string expectedFile)
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        if (secrets is not null) File.WriteAllText(paths.SecretsPath, secrets);
        using var configuration = new ConfigurationManager();

        var error = Assert.Throws<InvalidDataException>(() =>
            ConfigReloadBaseline.Load(configuration, paths, Encoding.UTF8.GetBytes(config)));

        Assert.StartsWith("Couldn't read " + expectedFile + ":", DaemonConfigurationSources.DescribeLoadFailure(error));
    }

    [Fact]
    public void Missing_empty_and_unreadable_files_remain_distinct()
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        Assert.Null(ConfigReloadBaseline.ReadNetclawJson(paths));
        paths.EnsureDirectoriesExist();
        Assert.Null(ConfigReloadBaseline.ReadNetclawJson(paths));
        Assert.Null(ConfigReloadBaseline.HashNetclawJson(null));
        File.WriteAllBytes(paths.NetclawConfigPath, []);
        var bytes = ConfigReloadBaseline.ReadNetclawJson(paths);
        Assert.NotNull(bytes);
        Assert.Empty(bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), ConfigReloadBaseline.HashNetclawJson(bytes));
        File.Delete(paths.NetclawConfigPath);
        Directory.CreateDirectory(paths.NetclawConfigPath);
        var error = Assert.Throws<InvalidDataException>(() => ConfigReloadBaseline.ReadNetclawJson(paths));
        Assert.StartsWith("Couldn't read netclaw.json:", DaemonConfigurationSources.DescribeLoadFailure(error));
    }

    [Fact]
    public void OAuth_persistence_changes_secrets_and_preserves_netclaw_bytes()
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, """{ "Providers": { "copilot": { "Type": "github-copilot" } } }""");
        var before = File.ReadAllBytes(paths.NetclawConfigPath);

        OAuthTokenPersistence.PersistTokens(paths, "copilot", new OAuthDeviceFlowResult(
            new SensitiveString("synthetic-access"), new SensitiveString("synthetic-refresh"),
            new DateTimeOffset(2026, 6, 1, 1, 0, 0, TimeSpan.Zero)), new NullSecretsProtector());

        Assert.Equal(before, File.ReadAllBytes(paths.NetclawConfigPath));
        using var configuration = new ConfigurationManager();
        configuration.AddNetclawDaemonSources(paths);
        Assert.Equal("synthetic-access", configuration["Providers:copilot:OAuthAccessToken"]);
        Assert.Equal("synthetic-refresh", configuration["Providers:copilot:OAuthRefreshToken"]);
    }
}
