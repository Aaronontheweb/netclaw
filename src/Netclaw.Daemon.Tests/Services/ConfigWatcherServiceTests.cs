// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
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
    private readonly ConfigFileWatch _watch;
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
        _watch = new ConfigFileWatch(_paths);
        _sut = CreateWatcher(_watch);
    }

    private ConfigWatcherService CreateWatcher(ConfigFileWatch watch)
    {
        var plugins = _services.GetServices<ILlmProviderPlugin>();

        using var configuration = new ConfigurationManager();
        var lowerPriorityConfiguration = configuration.AsEnumerable()
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        return new ConfigWatcherService(
            _paths,
            watch,
            _time,
            _restartCoordinator,
            _rejectedConfig,
            plugins,
            new FakeCapabilityResolver(null),
            NullLoggerFactory.Instance,
            _logger,
            _lifetime,
            lowerPriorityConfiguration);
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
    public async Task Start_without_a_file_event_leaves_no_reload_or_restart()
    {
        await _sut.StartAsync(TestContext.Current.CancellationToken);
        var queuedWork = !_sut.PendingReload.IsCompleted;
        _lifetime.NotifyStarted();
        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await _sut.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(queuedWork);
        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.Null(_rejectedConfig.Reason);
    }

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
    [InlineData("""{ broken json """)]
    [InlineData("""{ "Providers": {} } /* unclosed comment""")]
    [InlineData("""{ "Providers": {},, }""")]
    public async Task InvalidJson_DoesNotTriggerRestart(string config)
    {
        File.WriteAllText(_paths.NetclawConfigPath, config);
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
        await _sut.StartAsync(TestContext.Current.CancellationToken);
        _lifetime.NotifyStarted();
        // An atomic-replace write (write-temp then rename) surfaces as a Renamed
        // event whose new name is the watched config file.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;

        _watch.OnFileRenamed(this, new RenamedEventArgs(
            WatcherChangeTypes.Renamed, configDir, "netclaw.json", "netclaw.json.tmp.0a1b2c3d"));

        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task InPlaceWrite_TriggersReload()
    {
        await _sut.StartAsync(TestContext.Current.CancellationToken);
        _lifetime.NotifyStarted();
        // Regression: a direct in-place write (e.g. a shell > redirect) surfaces
        // as a Changed event and must still trigger a reload.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;

        _watch.OnFileChanged(this, new FileSystemEventArgs(
            WatcherChangeTypes.Changed, configDir, "netclaw.json"));

        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task OAuth_token_refresh_requests_no_daemon_restart()
    {
        await _sut.StartAsync(TestContext.Current.CancellationToken);
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
            _watch.OnFileRenamed(this, new RenamedEventArgs(
                WatcherChangeTypes.Renamed, _paths.ConfigDirectory, name, name + ".tmp.0a1b2c3d"));
        }

        var queuedWork = !_sut.PendingReload.IsCompleted;
        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;
        await _sut.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(queuedWork);
        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.Null(_rejectedConfig.Reason);
    }

    public enum StartupSyntax
    {
        StrictJson,
        Comments,
        TrailingComma
    }

    [Theory]
    [InlineData(StartupSyntax.StrictJson)]
    [InlineData(StartupSyntax.Comments)]
    [InlineData(StartupSyntax.TrailingComma)]
    public async Task Buffered_edit_uses_canonical_json_syntax(StartupSyntax syntax)
    {
        var candidate = syntax switch
        {
            StartupSyntax.StrictJson => """{ "Daemon": { "Host": "localhost" } }""",
            StartupSyntax.Comments => """
                // Synthetic config
                { "Daemon": { /* Synthetic host */ "Host": "localhost" } }
                """,
            StartupSyntax.TrailingComma => """{ "Daemon": { "Host": "localhost", }, }""",
            _ => throw new ArgumentOutOfRangeException(nameof(syntax), syntax, null)
        };
        File.WriteAllText(_paths.NetclawConfigPath, candidate);
        _watch.OnFileChanged(this, new FileSystemEventArgs(
            WatcherChangeTypes.Changed, _paths.ConfigDirectory, "netclaw.json"));

        using (var configuration = new ConfigurationManager())
        {
            configuration.AddNetclawDaemonSources(_paths);
            Assert.Equal("localhost", configuration["Daemon:Host"]);
        }

        await _sut.StartAsync(TestContext.Current.CancellationToken);
        _lifetime.NotifyStarted();
        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await _sut.StopAsync(TestContext.Current.CancellationToken);

        Assert.Null(_rejectedConfig.Reason);
        Assert.Equal(0, _logger.Warnings);
        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Theory]
    [InlineData("banana", """{ "Providers": { "p": { "Type": "ollama" } } }""", null)]
    [InlineData("ollama", """{ "Providers": { "p": { "Type": "banana" } } }""", "Unknown provider type 'banana'")]
    [InlineData("ollama", "{ invalid json", "Couldn't read secrets.json:")]
    public async Task Buffered_config_event_validates_the_full_source_chain(
        string baseProvider, string overlay, string? expectedRejection)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{ \"Providers\": { \"p\": { \"Type\": \"" + baseProvider + "\" } }, " + GoodModels + " }");
        File.WriteAllText(_paths.SecretsPath, overlay);
        _watch.OnFileChanged(this, new FileSystemEventArgs(
            WatcherChangeTypes.Changed, _paths.ConfigDirectory, "netclaw.json"));

        await _sut.StartAsync(TestContext.Current.CancellationToken);
        _lifetime.NotifyStarted();
        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await _sut.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedRejection is null ? 1 : 0, _restartCoordinator.RequestCount);
        if (expectedRejection is null)
            Assert.Null(_rejectedConfig.Reason);
        else
            Assert.Contains(expectedRejection, _rejectedConfig.Reason);
    }

    [Fact]
    public async Task Rapid_events_cancel_the_previous_debounce_and_request_one_restart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        await _sut.StartAsync(TestContext.Current.CancellationToken);
        _lifetime.NotifyStarted();
        var change = new FileSystemEventArgs(WatcherChangeTypes.Changed, _paths.ConfigDirectory, "netclaw.json");

        _watch.OnFileChanged(this, change);
        var first = _sut.PendingReload;
        _time.Advance(_sut.DebounceInterval / 2);
        _watch.OnFileChanged(this, change);
        var latest = _sut.PendingReload;
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, _restartCoordinator.RequestCount);

        _time.Advance(_sut.DebounceInterval);
        await latest.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await _sut.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Buffered_event_waits_for_application_start_after_debounce(bool startApplication)
    {
        var token = TestContext.Current.CancellationToken;
        File.WriteAllText(_paths.NetclawConfigPath, "{ invalid json");
        _watch.OnFileChanged(this, new FileSystemEventArgs(
            WatcherChangeTypes.Changed, _paths.ConfigDirectory, "netclaw.json"));
        var context = new QueuedSynchronizationContext();
        Task start = Task.CompletedTask;
        context.Run(() => start = _sut.StartAsync(token));
        await start;
        var pendingReload = _sut.PendingReload;

        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            _time.Advance(_sut.DebounceInterval);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        // This callback completes the debounce and reaches the application-start gate.
        await context.RunNextAsync(token);
        Assert.False(pendingReload.IsCompleted);
        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.Null(_rejectedConfig.Reason);
        Assert.Equal(0, _logger.Warnings);

        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        Task transition = Task.CompletedTask;
        previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            if (startApplication)
                _lifetime.NotifyStarted();
            else
                transition = _sut.StopAsync(token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await transition;
        await context.RunNextAsync(token);
        await pendingReload.WaitAsync(TimeSpan.FromSeconds(10), token);
        if (!startApplication)
            _lifetime.NotifyStarted();
        await _sut.StopAsync(token);

        Assert.True(pendingReload.IsCompletedSuccessfully);
        Assert.Equal(startApplication ? 1 : 0, _restartCoordinator.RequestCount);
        Assert.Null(_rejectedConfig.Reason);
        Assert.Equal(0, _logger.Warnings);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _callbacks =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();

        public override void Post(SendOrPostCallback callback, object? state)
            => _callbacks.Writer.TryWrite((callback, state));

        public void Run(Action action)
        {
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                action();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public async Task RunNextAsync(CancellationToken cancellationToken)
        {
            var (callback, state) = await _callbacks.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Run(() => callback(state));
        }
    }

    [Fact]
    public async Task Stop_before_application_started_cancels_the_buffered_event_and_detaches()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var change = new FileSystemEventArgs(WatcherChangeTypes.Changed, _paths.ConfigDirectory, "netclaw.json");
        _watch.OnFileChanged(this, change);
        await _sut.StartAsync(TestContext.Current.CancellationToken);
        var pendingReload = _sut.PendingReload;

        await _sut.StopAsync(TestContext.Current.CancellationToken);
        await pendingReload.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _lifetime.NotifyStarted();
        _watch.OnFileChanged(this, change);
        var queuedWork = !_sut.PendingReload.IsCompleted;
        _time.Advance(_sut.DebounceInterval);

        Assert.False(queuedWork);
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
        _watch.Dispose();
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
