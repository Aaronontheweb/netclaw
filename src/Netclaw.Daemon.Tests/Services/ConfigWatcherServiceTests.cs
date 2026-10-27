// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Services;
using Netclaw.Daemon.Tests.Providers;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;


namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigWatcherServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeRestartCoordinator _restartCoordinator;
    private readonly FakeTimeProvider _time = new();
    private readonly RejectedConfigState _rejectedConfig = new();
    private readonly CapturingLogger _logger = new();
    private readonly FakeApplicationLifetime _lifetime = new();
    private readonly ServiceProvider _services;
    private readonly ConfigWatcherService _sut;

    public ConfigWatcherServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        _restartCoordinator = new FakeRestartCoordinator();

        var services = new ServiceCollection();
        services.AddSingleton(_paths);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLlmProviders();
        _services = services.BuildServiceProvider();
        _sut = CreateWatcher(new NullSecretsProtector());
    }

    private ConfigWatcherService CreateWatcher(ISecretsProtector protector)
    {
        var plugins = _services.GetServices<ILlmProviderPlugin>();

        using var configuration = new ConfigurationManager();
        var lowerPriorityConfiguration = configuration.AsEnumerable()
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        configuration.AddNetclawDaemonSources(_paths);
        var generationConfiguration = configuration.AsEnumerable()
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        return new ConfigWatcherService(
            _paths,
            protector,
            _time,
            _restartCoordinator,
            _rejectedConfig,
            plugins,
            new FakeCapabilityResolver(null),
            NullLoggerFactory.Instance,
            _logger,
            _lifetime,
            lowerPriorityConfiguration,
            generationConfiguration);
    }

    private sealed class CapturingLogger : ILogger<ConfigWatcherService>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings++;
        }
    }

    private const string GoodModels = "\"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\" } }, \"Roles\": { \"Main\": \"d\" } }";

    [Fact]
    public async Task ValidConfigChange_TriggersRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Theory]
    [InlineData("""{ "Models": { "Main": { "Provider": "p", "ModelId": "m" }, "Roles": { "Main": "d" } } }""")]
    [InlineData("""{ "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" } } } }""")]
    [InlineData("""{ "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" } }, "Roles": { "Main": "nope" } } }""")]
    // Valid for the resolver, rejected by the rest of the startup check.
    [InlineData("""{ "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m", "ContextWindow": 100 } }, "Roles": { "Main": "d" } } }""")]
    [InlineData("""{ "Providers": { "p": { "Type": "ollama" } }, "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" }, "f": { "Provider": "zzz", "ModelId": "m" } }, "Roles": { "Main": "d", "Fallback": "f" } } }""")]
    public async Task InvalidModelsSection_DoesNotTriggerRestartAndIsReported(string config)
    {
        File.WriteAllText(_paths.NetclawConfigPath, config);

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.NotNull(_rejectedConfig.Reason);
    }

    [Fact]
    public async Task ValidEditAfterAnInvalidOne_IsAppliedAndClearsTheRejection()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Models": { "Roles": { "Main": "d" } } }""");
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.NotNull(_rejectedConfig.Reason);

        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": { "p": { "Type": "ollama" } }, "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" } }, "Roles": { "Main": "d" } } }""");
        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Null(_rejectedConfig.Reason);
        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Theory]
    [InlineData("""{ "Models": { "Main": { "Provider": "p", "ModelId": "m" } } }""")]
    [InlineData("""{ "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" } }, "Roles": { "Main": "d" } } }""")]
    public async Task ValidModelsSection_TriggersRestart(string config)
    {
        File.WriteAllText(_paths.NetclawConfigPath, config);

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
        Assert.Null(_rejectedConfig.Reason);
    }

    [Theory]
    [InlineData("secrets.json")]
    [InlineData("mcp-oauth-metadata.json")]
    [InlineData("random.txt")]
    [InlineData(null)]
    public void NonConfigFiles_AreNotWatched(string? fileName)
    {
        Assert.False(ConfigWatcherService.IsWatchedFile(fileName));
    }

    [Fact]
    public void NetclawJson_IsWatched()
    {
        Assert.True(ConfigWatcherService.IsWatchedFile("netclaw.json"));
    }

    [Fact]
    public async Task InvalidJson_DoesNotTriggerRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ broken json """);
        // secrets.json doesn't exist — that's fine (optional)

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.StartsWith("netclaw.json is not valid JSON", _rejectedConfig.Reason);
        Assert.DoesNotContain('\n', _rejectedConfig.Reason!);
    }

    [Fact]
    public async Task DuplicateModelsKeys_AreRejectedWithTheReason()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Models": { "Roles": { "Main": "a" } }, "models": { "Roles": { "Main": "b" } } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.StartsWith("Couldn't read netclaw.json:", _rejectedConfig.Reason);
        Assert.DoesNotContain('\n', _rejectedConfig.Reason!);
    }

    // Each of these passes the Models resolver and fails only where startup builds the provider.
    [Theory]
    [InlineData("""{ "Type": "banana" }""", "Unknown provider type 'banana'. Supported: ollama,")]
    [InlineData("""{ "Type": "openai" }""", "requires authentication")]
    [InlineData("""{ "Type": "anthropic" }""", "requires authentication")]
    [InlineData("""{ "Type": "openai-compatible" }""", "Set Providers:p:Endpoint to a URL.")]
    [InlineData("""{ "Type": "ollama", "AuthMethod": "banana" }""", "Providers:p is invalid: Failed to convert configuration value 'banana' at 'Providers:p:AuthMethod'")]
    [InlineData("""{ "Type": "ollama", "VendorOptions": "x" }""", "Providers:p is invalid: Providers:<name>:VendorOptions must be an object.")]
    [InlineData("""{ "Type": "ollama", "OAuthTokenExpiry": "garbage" }""", "at 'Providers:p:OAuthTokenExpiry'")]
    public async Task ProviderThatStartupCannotBuild_IsRejectedWithTheReason(string provider, string expected)
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"Providers\": { \"p\": " + provider + " }, " + GoodModels + " }");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.Contains(expected, _rejectedConfig.Reason);
        Assert.DoesNotContain('\n', _rejectedConfig.Reason!);
        Assert.DoesNotContain("   at ", _rejectedConfig.Reason);
    }

    [Fact]
    public async Task thinking_with_ollama_disable_thinking_is_rejected_on_reload()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "Providers": { "p": { "Type": "ollama", "VendorOptions": { "DisableThinking": true } } },
              "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m", "Thinking": "Low" } }, "Roles": { "Main": "d" } } }
            """);

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.Contains("DisableThinking", _rejectedConfig.Reason);
    }

    [Fact]
    public async Task RejectionFollowsTheLatestEdit_NotTheFirstReason()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": { "p": { "Type": "ollama" } }, "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m", "ContextWindow": 100 } }, "Roles": { "Main": "d" } } }""");
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.Contains("ContextWindow", _rejectedConfig.Reason);

        // Models is fixed; something else is now wrong.
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"Providers\": { \"p\": { \"Type\": \"ollama\", \"AuthMethod\": \"banana\" } }, " + GoodModels + " }");
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.DoesNotContain("ContextWindow", _rejectedConfig.Reason);
        Assert.Contains("Providers:p", _rejectedConfig.Reason);

        File.WriteAllText(_paths.NetclawConfigPath, """{ broken json """);
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.StartsWith("netclaw.json is not valid JSON", _rejectedConfig.Reason);
        Assert.Equal(0, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task UnchangedInvalidFile_WarnsOnce()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Models": { "Roles": { "Main": "d" } } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);
        await _sut.ApplyReloadAsync(CancellationToken.None);
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.Equal(1, _logger.Warnings);

        File.WriteAllText(_paths.NetclawConfigPath, """{ "Models": { "Roles": { "Main": "e" } } }""");
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.Equal(2, _logger.Warnings);
    }

    [Fact]
    public async Task MissingConfigFiles_TriggersRestart()
    {
        // Both files are optional in the config chain — missing = valid
        Assert.False(File.Exists(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task RestartCoordinatorFailure_DoesNotLeaveIngressClosed()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        _restartCoordinator.ThrowOnRequest = true;

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task DaemonSectionChange_TriggersRestart()
    {
        // Regression guard for #1279: Daemon-section changes (bind address, exposure
        // mode) are now applied via the coordinated in-process restart rather than
        // skipped. The restart rebuilds the host and re-binds, and the init wizard
        // relies on this so that writing a new exposure mode actually takes effect.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Daemon": { "Port": 9999, "ExposureMode": "reverse-proxy" } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    // File-event tests — drive the watcher's event handlers directly with
    // synthesized event args. Real FileSystemWatcher delivery is OS-dependent
    // and cannot be observed deterministically (especially the latency and
    // event classification of an atomic-replace move on Windows), so the
    // handler -> debounce -> reload pipeline is exercised in isolation and the
    // debounce is virtualized through the injected FakeTimeProvider.

    [Fact]
    public async Task AtomicReplace_TriggersReload()
    {
        _lifetime.NotifyStarted();
        // An atomic-replace write (write-temp then rename) surfaces as a Renamed
        // event whose new name is the watched config file.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;

        _sut.OnFileRenamed(this, new RenamedEventArgs(
            WatcherChangeTypes.Renamed, configDir, "netclaw.json", "netclaw.json.tmp.0a1b2c3d"));

        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task InPlaceWrite_TriggersReload()
    {
        _lifetime.NotifyStarted();
        // Regression: a direct in-place write (e.g. a shell > redirect) surfaces
        // as a Changed event and must still trigger a reload.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;

        _sut.OnFileChanged(this, new FileSystemEventArgs(
            WatcherChangeTypes.Changed, configDir, "netclaw.json"));

        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task OAuth_token_refresh_requests_no_daemon_restart()
    {
        _lifetime.NotifyStarted();
        File.WriteAllText(
            _paths.NetclawConfigPath,
            """{ "Providers": { "copilot": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-06-01T00:00:00.0000000+00:00" } } }""");
        var before = SnapshotConfigDirectory();

        OAuthTokenPersistence.PersistTokens(
            _paths,
            "copilot",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-1"),
                new SensitiveString("refresh-1"),
                DateTimeOffset.Parse("2026-06-01T01:00:00+00:00")),
            new NullSecretsProtector());

        var after = SnapshotConfigDirectory();
        var written = after.Where(file => !before.TryGetValue(file.Key, out var hash) || hash != file.Value)
            .Select(file => file.Key)
            .ToList();
        Assert.Contains("secrets.json", written);
        foreach (var name in written)
        {
            _sut.OnFileRenamed(this, new RenamedEventArgs(
                WatcherChangeTypes.Renamed, _paths.ConfigDirectory, name, name + ".tmp.0a1b2c3d"));
        }

        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.Null(_rejectedConfig.Reason);
    }

    public enum StartupCredentialCase
    {
        Plaintext,
        EncryptedUnchangedOtherSecrets,
        PlaintextMigrationUnchangedOtherSecrets,
        EncryptedChangedOtherApiKey,
        ImplicitCopilotOAuth,
        InvalidEncryptedSecret,
        OpaqueVendorValue,
        MultiAuthApiKey,
        MultiAuthOAuth,
        LegacyApiKeyNull,
        LegacyApiKeySourceExposure
    }

    [Theory]
    [InlineData(false, false, 0, StartupCredentialCase.Plaintext)]
    [InlineData(true, false, 0, StartupCredentialCase.Plaintext)]
    [InlineData(false, true, 1, StartupCredentialCase.Plaintext)]
    [InlineData(true, true, 1, StartupCredentialCase.Plaintext)]
    [InlineData(true, false, 0, StartupCredentialCase.EncryptedUnchangedOtherSecrets)]
    [InlineData(true, false, 0, StartupCredentialCase.PlaintextMigrationUnchangedOtherSecrets)]
    [InlineData(true, false, 1, StartupCredentialCase.EncryptedChangedOtherApiKey)]
    [InlineData(true, false, 0, StartupCredentialCase.ImplicitCopilotOAuth)]
    [InlineData(true, false, 0, StartupCredentialCase.InvalidEncryptedSecret)]
    [InlineData(true, false, 1, StartupCredentialCase.OpaqueVendorValue)]
    [InlineData(true, false, 1, StartupCredentialCase.MultiAuthApiKey)]
    [InlineData(true, false, 0, StartupCredentialCase.MultiAuthOAuth)]
    [InlineData(true, false, 1, StartupCredentialCase.LegacyApiKeyNull)]
    [InlineData(true, false, 1, StartupCredentialCase.LegacyApiKeySourceExposure)]
    public async Task Startup_reconciliation_preserves_the_OAuth_refresh_contract(
        bool refreshTokens, bool changeHostOverlay, int expectedRestarts, StartupCredentialCase credentialCase)
    {
        var protectOtherSecrets = credentialCase is StartupCredentialCase.EncryptedUnchangedOtherSecrets
            or StartupCredentialCase.PlaintextMigrationUnchangedOtherSecrets
            or StartupCredentialCase.EncryptedChangedOtherApiKey;
        var initiallyEncrypted = credentialCase is StartupCredentialCase.EncryptedUnchangedOtherSecrets
            or StartupCredentialCase.EncryptedChangedOtherApiKey;
        ISecretsProtector protector = protectOtherSecrets || credentialCase is
            StartupCredentialCase.InvalidEncryptedSecret or StartupCredentialCase.OpaqueVendorValue
            ? SecretsProtection.CreateProtector(_paths)
            : new NullSecretsProtector();
        var config = JsonNode.Parse("""
            {
              "Daemon": { "Host": "127.0.0.1" },
              "Providers": { "copilot": { "Type": "github-copilot", "AuthMethod": "OAuthDevice" } }
            }
            """)!.AsObject();
        if (credentialCase == StartupCredentialCase.ImplicitCopilotOAuth)
            config["Providers"]!["copilot"]!.AsObject().Remove("AuthMethod");
        if (protectOtherSecrets)
            config["Providers"]!["other"] = new JsonObject { ["Type"] = "openai", ["AuthMethod"] = "ApiKey" };
        var expectedProvider = ConfigureStartupCredentialControl(config, credentialCase);
        File.WriteAllText(_paths.NetclawConfigPath, config.ToJsonString());

        var initialSecrets = JsonNode.Parse("""
            {
              "Providers": {
                "copilot": {
                  "ApiKey": "synthetic-retired-key",
                  "OAuthAccessToken": "synthetic-access-before",
                  "OAuthRefreshToken": "synthetic-refresh-before",
                  "OAuthAccountId": "synthetic-account-before",
                  "OAuthTokenExpiry": "2026-06-01T00:00:00.0000000+00:00"
                }
              }
            }
            """)!.AsObject();
        if (protectOtherSecrets)
        {
            initialSecrets["Providers"]!["other"] = new JsonObject { ["ApiKey"] = "synthetic-other-key-before" };
            initialSecrets["Search"] = new JsonObject { ["BraveApiKey"] = "synthetic-search-key" };
        }
        SecretsFileWriter.Write(_paths.SecretsPath, initialSecrets.ToJsonString(),
            initiallyEncrypted ? protector : new NullSecretsProtector());
        var storedBefore = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!;
        var originalConfig = File.ReadAllBytes(_paths.NetclawConfigPath);
        using (var generation = new ConfigurationManager())
        {
            generation.AddNetclawDaemonSources(_paths);
            var initialProvider = ProviderConfigurationLoader.Load(generation.GetSection("Providers"))["copilot"];
            AssertStartupApiKey(generation, initialProvider, storedBefore, protector, "synthetic-retired-key", true);
        }

        // Capture the generation before the refresh, through the same sources as Program.
        using var watcher = CreateWatcher(protector);
        if (refreshTokens)
        {
            OAuthTokenPersistence.PersistTokens(
                _paths,
                "copilot",
                new OAuthDeviceFlowResult(
                    new SensitiveString("synthetic-access-after"),
                    new SensitiveString("synthetic-refresh-after"),
                    new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
                    new SensitiveString("synthetic-account-after")),
                protector);
        }

        if (protectOtherSecrets)
        {
            var storedAfter = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!;
            foreach (var path in new[] { new[] { "Providers", "other", "ApiKey" }, new[] { "Search", "BraveApiKey" } })
            {
                var before = path.Aggregate(storedBefore, (node, key) => node[key]!).GetValue<string>();
                var after = path.Aggregate(storedAfter, (node, key) => node[key]!).GetValue<string>();
                Assert.Equal(initiallyEncrypted, ISecretsProtector.IsEncrypted(before));
                Assert.True(ISecretsProtector.IsEncrypted(after));
                Assert.False(string.Equals(before, after, StringComparison.Ordinal), "The stored secret must change after the refresh.");
                var plaintextBefore = initiallyEncrypted ? protector.Unprotect(before) : before;
                Assert.True(string.Equals(plaintextBefore, protector.Unprotect(after), StringComparison.Ordinal),
                    "The refresh must preserve the other secret's plaintext.");
            }
        }

        if (credentialCase == StartupCredentialCase.EncryptedChangedOtherApiKey)
        {
            SecretsFileWriter.Update(_paths.SecretsPath, (root, _) =>
            {
                root["Providers"]!["other"]!["ApiKey"] = "synthetic-other-key-after";
                return (root, 0);
            }, protector, cancellationToken: TestContext.Current.CancellationToken);
        }
        ApplyStartupCredentialControl(credentialCase, protector);

        if (changeHostOverlay)
        {
            var secrets = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
            secrets["Daemon"] = new JsonObject { ["Host"] = "localhost" };
            File.WriteAllText(_paths.SecretsPath, secrets.ToJsonString());
        }

        Assert.Equal(originalConfig, File.ReadAllBytes(_paths.NetclawConfigPath));
        using var candidate = new ConfigurationManager();
        candidate.AddNetclawDaemonSources(_paths);
        Assert.Equal(changeHostOverlay ? "localhost" : "127.0.0.1", candidate["Daemon:Host"]);
        var provider = ProviderConfigurationLoader.Load(candidate.GetSection("Providers"))["copilot"];
        Assert.Equal(expectedProvider.Type, provider.Type);
        Assert.Equal(expectedProvider.Auth, provider.AuthMethod);
        if (credentialCase == StartupCredentialCase.ImplicitCopilotOAuth)
            Assert.False(candidate.GetSection("Providers:copilot:AuthMethod").Exists());

        AssertStartupCredentialControl(candidate, provider, credentialCase, refreshTokens, protector);
        if (refreshTokens)
        {
            foreach (var (token, expected) in new[]
                     {
                         (provider.OAuthAccessToken, "synthetic-access-after"),
                         (provider.OAuthRefreshToken, "synthetic-refresh-after"),
                         (provider.OAuthAccountId, "synthetic-account-after")
                     })
            {
                Assert.NotNull(token);
                var actual = ISecretsProtector.IsEncrypted(token.Value) ? protector.Unprotect(token.Value) : token.Value;
                Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), "The candidate must contain the refreshed token.");
            }
            Assert.Equal(new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero), provider.OAuthTokenExpiry);
        }
        if (protectOtherSecrets)
        {
            // Use the owned protector. The process-wide converter hook belongs to host setup.
            var plaintext = JsonNode.Parse(SecretsFileWriter.DecryptJsonLeaves(File.ReadAllText(_paths.SecretsPath), protector))!;
            var expectedKey = credentialCase == StartupCredentialCase.EncryptedChangedOtherApiKey
                ? "synthetic-other-key-after" : "synthetic-other-key-before";
            Assert.True(string.Equals(expectedKey, plaintext["Providers"]!["other"]!["ApiKey"]!.GetValue<string>(), StringComparison.Ordinal),
                "The candidate must contain the expected provider key.");
            Assert.True(string.Equals("synthetic-search-key", plaintext["Search"]!["BraveApiKey"]!.GetValue<string>(), StringComparison.Ordinal),
                "The candidate must contain the unchanged search key.");
        }

        // Complete every write before StartAsync, so only startup reconciliation can request a restart.
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        _time.Advance(watcher.DebounceInterval);
        var pendingReload = watcher.PendingReload;
        Assert.False(pendingReload.IsCompleted);
        Assert.Equal(0, _restartCoordinator.RequestCount);

        _lifetime.NotifyStarted();
        await pendingReload.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await watcher.StopAsync(TestContext.Current.CancellationToken);

        if (credentialCase == StartupCredentialCase.InvalidEncryptedSecret)
            Assert.NotNull(_rejectedConfig.Reason);
        else
            Assert.Null(_rejectedConfig.Reason);
        Assert.Equal(expectedRestarts, _restartCoordinator.RequestCount);
    }

    private (string Type, AuthMethod Auth) ConfigureStartupCredentialControl(
        JsonObject config, StartupCredentialCase credentialCase)
    {
        var provider = config["Providers"]!["copilot"]!.AsObject();
        var expected = credentialCase switch
        {
            StartupCredentialCase.MultiAuthApiKey => (Type: "openai", Auth: AuthMethod.ApiKey),
            StartupCredentialCase.MultiAuthOAuth => (Type: "openai", Auth: AuthMethod.OAuthPkce),
            StartupCredentialCase.ImplicitCopilotOAuth => (Type: "github-copilot", Auth: AuthMethod.None),
            _ => (Type: "github-copilot", Auth: AuthMethod.OAuthDevice)
        };
        provider["Type"] = expected.Type;
        if (credentialCase != StartupCredentialCase.ImplicitCopilotOAuth)
            provider["AuthMethod"] = expected.Auth.ToString();

        var descriptor = _services.GetServices<ILlmProviderPlugin>().Last(plugin => plugin.TypeKey == expected.Type);
        if (credentialCase is StartupCredentialCase.MultiAuthApiKey or StartupCredentialCase.MultiAuthOAuth)
        {
            var auth = Assert.IsType<MultiAuth>(descriptor.Auth);
            Assert.Contains(expected.Auth, auth.SupportedAuthMethods);
        }
        if (credentialCase == StartupCredentialCase.OpaqueVendorValue)
            provider["VendorOptions"] = new JsonObject { ["Marker"] = "ENC:synthetic-vendor-before" };
        if (credentialCase == StartupCredentialCase.LegacyApiKeySourceExposure)
            provider["ApiKey"] = "synthetic-base-key";
        return expected;
    }

    private void ApplyStartupCredentialControl(StartupCredentialCase credentialCase, ISecretsProtector protector)
    {
        if (credentialCase is StartupCredentialCase.InvalidEncryptedSecret or StartupCredentialCase.OpaqueVendorValue)
        {
            var value = credentialCase == StartupCredentialCase.InvalidEncryptedSecret
                ? "ENC:synthetic-invalid-secret" : "ENC:synthetic-vendor-after";
            Assert.True(ISecretsProtector.IsEncrypted(value));
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(value));
            var secrets = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
            if (credentialCase == StartupCredentialCase.InvalidEncryptedSecret)
                secrets["Search"] = new JsonObject { ["BraveApiKey"] = value };
            else
                secrets["Providers"]!["copilot"]!["VendorOptions"] = new JsonObject { ["Marker"] = value };
            // Preserve the invalid or opaque bytes for the watcher to classify.
            File.WriteAllText(_paths.SecretsPath, secrets.ToJsonString());
        }
        if (credentialCase is StartupCredentialCase.MultiAuthApiKey or StartupCredentialCase.LegacyApiKeyNull)
        {
            SecretsFileWriter.Update(_paths.SecretsPath, (root, _) =>
            {
                root["Providers"]!["copilot"]!["ApiKey"] = credentialCase == StartupCredentialCase.LegacyApiKeyNull
                    ? null : "synthetic-retired-key";
                return (root, 0);
            }, protector, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private void AssertStartupCredentialControl(
        IConfiguration candidate, ProviderEntry provider, StartupCredentialCase credentialCase,
        bool refreshTokens, ISecretsProtector protector)
    {
        var stored = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!;
        var expectedKey = credentialCase switch
        {
            StartupCredentialCase.MultiAuthApiKey => "synthetic-retired-key",
            StartupCredentialCase.LegacyApiKeySourceExposure => "synthetic-base-key",
            _ => refreshTokens ? null : "synthetic-retired-key"
        };
        var storedKeyPresent = !refreshTokens || credentialCase is
            StartupCredentialCase.MultiAuthApiKey or StartupCredentialCase.LegacyApiKeyNull;
        AssertStartupApiKey(candidate, provider, stored, protector, expectedKey, storedKeyPresent);
        if (credentialCase == StartupCredentialCase.InvalidEncryptedSecret)
            Assert.True(string.Equals("ENC:synthetic-invalid-secret", candidate["Search:BraveApiKey"], StringComparison.Ordinal),
                "The candidate must retain the invalid encrypted value for rejection.");
        if (credentialCase == StartupCredentialCase.OpaqueVendorValue)
        {
            Assert.NotNull(provider.VendorOptions);
            Assert.True(provider.VendorOptions.ContainsKey("Marker"));
            Assert.True(string.Equals("ENC:synthetic-vendor-after", provider.VendorOptions["Marker"]!.GetValue<string>(), StringComparison.Ordinal),
                "The provider loader must preserve the opaque vendor value.");
            Assert.True(string.Equals("ENC:synthetic-vendor-after", candidate["Providers:copilot:VendorOptions:Marker"], StringComparison.Ordinal),
                "The candidate must retain the opaque vendor value.");
        }
    }

    private static void AssertStartupApiKey(
        IConfiguration configuration, ProviderEntry provider, JsonNode stored,
        ISecretsProtector protector, string? expectedKey, bool storedKeyPresent)
    {
        Assert.Equal(storedKeyPresent, stored["Providers"]!["copilot"]!.AsObject().ContainsKey("ApiKey"));
        Assert.Equal(storedKeyPresent || expectedKey is not null,
            configuration.AsEnumerable().Any(entry => entry.Key == "Providers:copilot:ApiKey"));
        if (expectedKey is null)
        {
            Assert.Null(provider.ApiKey);
            Assert.Null(configuration["Providers:copilot:ApiKey"]);
        }
        else
        {
            Assert.NotNull(provider.ApiKey);
            var actual = ISecretsProtector.IsEncrypted(provider.ApiKey.Value)
                ? protector.Unprotect(provider.ApiKey.Value) : provider.ApiKey.Value;
            Assert.True(string.Equals(expectedKey, actual, StringComparison.Ordinal),
                "The provider loader must preserve the expected API key.");
        }
    }

    [Fact]
    public async Task Stop_before_application_started_cancels_startup_reconciliation()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        await _sut.StartAsync(TestContext.Current.CancellationToken);
        _time.Advance(_sut.DebounceInterval);
        var pendingReload = _sut.PendingReload;
        Assert.False(pendingReload.IsCompleted);
        Assert.Equal(0, _restartCoordinator.RequestCount);

        await _sut.StopAsync(TestContext.Current.CancellationToken);
        await pendingReload.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _lifetime.NotifyStarted();

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.Null(_rejectedConfig.Reason);
    }

    private Dictionary<string, string> SnapshotConfigDirectory()
        => Directory.EnumerateFiles(_paths.ConfigDirectory)
            .ToDictionary(
                file => Path.GetFileName(file)!,
                file => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));

    public void Dispose()
    {
        _sut.Dispose();
        _lifetime.Dispose();
        _services.Dispose();
        _dir.Dispose();
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void NotifyStarted() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
        }
    }

    private sealed class FakeRestartCoordinator : IDaemonRestartCoordinator
    {
        public int RequestCount { get; private set; }

        public bool ThrowOnRequest { get; set; }

        public Task RequestConfigRestartAsync(CancellationToken cancellationToken)
        {
            RequestCount++;

            if (ThrowOnRequest)
                throw new InvalidOperationException("boom");

            return Task.CompletedTask;
        }
    }
}
