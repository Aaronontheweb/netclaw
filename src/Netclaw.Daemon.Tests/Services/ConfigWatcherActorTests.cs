// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Daemon.Tests.Providers;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Daemon.Services.ConfigWatcherActor;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigWatcherActorTests(ITestOutputHelper output) : TestKit(output: output), IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly DisposableTempDir _directory = new();
    private readonly RejectedConfigState _rejected = new();
    private readonly RestartCoordinator _restart = new();
    private readonly ApplicationLifetime _lifetime = new();
    private IEnumerable<ILlmProviderPlugin> _plugins = [];
    private NetclawPaths Paths => new(_directory.Path);
    private const string Models = "\"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\" } }, \"Roles\": { \"Main\": \"d\" } }";

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        var paths = Paths;
        paths.EnsureDirectoriesExist();
        services.AddSingleton(paths);
        services.AddLlmProviders();
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        _plugins = provider.GetServices<ILlmProviderPlugin>().ToArray();
        builder.AddHocon(ConfigurationFactory.ParseString(
            "akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\""), HoconAddMode.Prepend);
    }

    private string? WriteCandidate(string? config, string? secrets)
    {
        Paths.EnsureDirectoriesExist();
        if (config is not null) File.WriteAllText(Paths.NetclawConfigPath, config);
        if (secrets is not null) File.WriteAllText(Paths.SecretsPath, secrets);
        return ConfigReloadBaseline.HashNetclawJson(ConfigReloadBaseline.ReadNetclawJson(Paths));
    }

    private IActorRef CreateActor(string? loadedHash) => Sys.ActorOf(Props.Create(() => new ConfigWatcherActor(
        Paths, new ConfigReloadBaseline(loadedHash, new Dictionary<string, string?>()), _restart, _rejected,
        _plugins, new FakeCapabilityResolver(null), NullLoggerFactory.Instance, TimeProvider.System, _lifetime)));

    private Task SettleAsync(IActorRef actor)
        => actor.Ask<ActorIdentity>(new Identify(1), Deadline, TestContext.Current.CancellationToken);

    private async Task AdvanceAsync(IActorRef actor, TimeSpan by)
    {
        await SettleAsync(actor);
        ((TestScheduler)Sys.Scheduler).Advance(by);
        await SettleAsync(actor);
    }

    [Theory]
    [InlineData("same", 0)]
    [InlineData("different", 1)]
    [InlineData("appeared", 1)]
    [InlineData("missing", 0)]
    [InlineData("read_error", 0)]
    public async Task Startup_reconciliation_uses_the_loaded_hash(string state, int expectedRestarts)
    {
        var current = WriteCandidate(state == "missing" ? null : "{}", null);
        if (state == "read_error")
        {
            File.Delete(Paths.NetclawConfigPath);
            Directory.CreateDirectory(Paths.NetclawConfigPath);
        }
        var loaded = state switch { "same" => current, "appeared" => null, _ => "older-hash" };
        var actor = CreateActor(loaded);
        actor.Tell(HostStarted.Instance);
        await AdvanceAsync(actor, Debounce);

        Assert.Equal(expectedRestarts, _restart.Count);
        if (state == "read_error") Assert.Contains("netclaw.json", _rejected.Reason);
        else Assert.Null(_rejected.Reason);
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Changed, "netclaw.json", null, 1)]
    [InlineData(WatcherChangeTypes.Created, "netclaw.json", null, 1)]
    [InlineData(WatcherChangeTypes.Renamed, "netclaw.json", "config.tmp", 1)]
    [InlineData(WatcherChangeTypes.Changed, "secrets.json", null, 0)]
    [InlineData(WatcherChangeTypes.Deleted, "netclaw.json", null, 0)]
    [InlineData(WatcherChangeTypes.Renamed, "config.old", "netclaw.json", 0)]
    [InlineData(WatcherChangeTypes.Changed, "other.txt", null, 0)]
    [InlineData(WatcherChangeTypes.Changed, null, null, 0)]
    public async Task File_events_preserve_the_contract_even_when_bytes_match_the_baseline(
        WatcherChangeTypes kind, string? name, string? oldName, int expectedRestarts)
    {
        var actor = CreateActor(WriteCandidate("{}", null));
        actor.Tell(HostStarted.Instance);
        actor.Tell(new ConfigChanged(kind, name, oldName));
        await AdvanceAsync(actor, Debounce);

        Assert.Equal(expectedRestarts, _restart.Count);
        Assert.Null(_rejected.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_due_reload_waits_for_host_start_or_actor_stop(bool stop)
    {
        WriteCandidate("{ broken json", null);
        var actor = CreateActor("older-hash");
        await AdvanceAsync(actor, Debounce);
        Assert.Null(_rejected.Reason);
        Assert.Equal(0, _restart.Count);

        if (stop)
        {
            await WatchAsync(actor);
            Sys.Stop(actor);
            await ExpectTerminatedAsync(actor, Deadline, cancellationToken: TestContext.Current.CancellationToken);
            _lifetime.NotifyStarted();
            ((TestScheduler)Sys.Scheduler).Advance(Debounce);
            Assert.Null(_rejected.Reason);
        }
        else
        {
            _lifetime.NotifyStarted();
            await SettleAsync(actor);
            Assert.StartsWith("Couldn't read netclaw.json:", _rejected.Reason);
        }
        Assert.Equal(0, _restart.Count);
    }

    [Fact]
    public async Task A_new_event_replaces_the_debounce_and_ignored_events_preserve_it()
    {
        var actor = CreateActor(WriteCandidate("{}", null));
        actor.Tell(HostStarted.Instance);
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
        await AdvanceAsync(actor, Debounce / 2);
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Created, "netclaw.json", null));
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Deleted, "netclaw.json", null));
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Renamed, "config.old", "netclaw.json"));
        await AdvanceAsync(actor, Debounce / 2);
        Assert.Equal(0, _restart.Count);
        await AdvanceAsync(actor, Debounce / 2);
        Assert.Equal(1, _restart.Count);
    }

    [Theory]
    [InlineData("{}", null, null)]
    [InlineData(null, null, null)]
    [InlineData("{ \"Daemon\": { \"Port\": 9999, \"ExposureMode\": \"reverse-proxy\" } }", null, null)]
    [InlineData("{ \"Models\": { \"Main\": { \"Provider\": \"p\", \"ModelId\": \"m\" } } }", null, null)]
    [InlineData("{ " + Models + " }", null, null)]
    [InlineData("{ \"Models\": { \"Main\": { \"Provider\": \"p\", \"ModelId\": \"m\" }, \"Roles\": { \"Main\": \"d\" } } }", null, "Models")]
    [InlineData("{ \"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\" } } } }", null, "Models")]
    [InlineData("{ \"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\" } }, \"Roles\": { \"Main\": \"nope\" } } }", null, "Models")]
    [InlineData("{ \"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\", \"ContextWindow\": 100 } }, \"Roles\": { \"Main\": \"d\" } } }", null, "ContextWindow")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"ollama\" } }, \"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\" }, \"f\": { \"Provider\": \"zzz\", \"ModelId\": \"m\" } }, \"Roles\": { \"Main\": \"d\", \"Fallback\": \"f\" } } }", null, "zzz")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"banana\" } }, " + Models + " }", null, "Unknown provider type 'banana'")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"openai\" } }, " + Models + " }", null, "requires authentication")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"anthropic\" } }, " + Models + " }", null, "requires authentication")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"openai-compatible\" } }, " + Models + " }", null, "Endpoint")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"ollama\", \"AuthMethod\": \"banana\" } }, " + Models + " }", null, "AuthMethod")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"ollama\", \"VendorOptions\": \"x\" } }, " + Models + " }", null, "VendorOptions")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"ollama\", \"OAuthTokenExpiry\": \"garbage\" } }, " + Models + " }", null, "OAuthTokenExpiry")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"ollama\", \"VendorOptions\": { \"DisableThinking\": true } } }, \"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\", \"Thinking\": \"Low\" } }, \"Roles\": { \"Main\": \"d\" } } }", null, "DisableThinking")]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"banana\" } }, " + Models + " }", "{ \"Providers\": { \"p\": { \"Type\": \"ollama\" } } }", null)]
    [InlineData("{ \"Providers\": { \"p\": { \"Type\": \"ollama\" } }, " + Models + " }", "{ \"Providers\": { \"p\": { \"Type\": \"banana\" } } }", "Unknown provider type 'banana'")]
    [InlineData("{}", "{ broken json", "Couldn't read secrets.json:")]
    [InlineData("{ broken json", null, "Couldn't read netclaw.json:")]
    public async Task Reload_validates_canonical_sources_models_and_providers(
        string? config, string? secrets, string? expectedReason)
    {
        var hash = WriteCandidate(config, secrets);
        _rejected.Reject("an earlier rejected edit");
        var actor = CreateActor(hash);
        actor.Tell(HostStarted.Instance);
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
        await AdvanceAsync(actor, Debounce);

        Assert.Equal(expectedReason is null ? 1 : 0, _restart.Count);
        if (expectedReason is null) Assert.Null(_rejected.Reason);
        else
        {
            Assert.Contains(expectedReason, _rejected.Reason);
            Assert.DoesNotContain('\n', _rejected.Reason!);
            Assert.DoesNotContain("   at ", _rejected.Reason);
        }
    }

    [Fact]
    public async Task Repeated_invalid_events_warn_once()
    {
        var actor = CreateActor(WriteCandidate("{ broken json", null));
        actor.Tell(HostStarted.Instance);
        await EventFilter.Warning(contains: "Config validation failed.").ExpectAsync(1, async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
                await AdvanceAsync(actor, Debounce);
            }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(0, _restart.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_admitted_restart_ignores_queued_events_and_stop_cancels_its_token(bool stop)
    {
        _restart.Hold = true;
        var actor = CreateActor(WriteCandidate("{}", null));
        await WatchAsync(actor);
        actor.Tell(HostStarted.Instance);
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
        try
        {
            await SettleAsync(actor);
            ((TestScheduler)Sys.Scheduler).Advance(Debounce);
            await _restart.Entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
            if (stop)
            {
                Sys.Stop(actor);
                await ExpectTerminatedAsync(actor, Deadline, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(_restart.Token.IsCancellationRequested);
            }
            else
            {
                _restart.Release.TrySetResult();
                await AdvanceAsync(actor, Debounce);
            }
            Assert.Equal(1, _restart.Count);
        }
        finally { _restart.Release.TrySetResult(); }
    }

    [Fact]
    public async Task A_failed_coordinator_allows_a_later_event_to_retry()
    {
        _restart.ThrowFirst = true;
        var actor = CreateActor(WriteCandidate("{}", null));
        actor.Tell(HostStarted.Instance);
        for (var i = 0; i < 2; i++)
        {
            actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
            await AdvanceAsync(actor, Debounce);
        }
        Assert.Equal(2, _restart.Count);
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { _lifetime.Dispose(); _directory.Dispose(); }
    }

    private sealed class ApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void NotifyStarted() => _started.Cancel();
        public void StopApplication() => throw new InvalidOperationException("The test actor could not start its watcher.");
        public void Dispose() => _started.Dispose();
    }

    private sealed class RestartCoordinator : IDaemonRestartCoordinator
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public bool Hold { get; set; }
        public bool ThrowFirst { get; set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RequestConfigRestartAsync(CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            var count = Interlocked.Increment(ref _count);
            Entered.TrySetResult();
            if (ThrowFirst && count == 1) throw new InvalidOperationException("The test restart failed.");
            return Hold ? Release.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
        }
    }
}
