// -----------------------------------------------------------------------
// <copyright file="DiscordGatewayLifecycleRetryTimeoutTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Transport;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Regression coverage for the 0.24.0-beta.2 Discord reliability incident:
/// when an auto-retry connect attempt timed out waiting for READY, the actor
/// treated <see cref="ActorRefs.Nobody"/> as a pending caller and silently
/// parked itself in CleanReconnectRequired — a state with no timers and no
/// transport-event exits — dropping all Discord traffic until restart.
/// Runs on a virtual-time scheduler so the 30s READY timeout can be driven
/// deterministically.
/// </summary>
public sealed class DiscordGatewayLifecycleRetryTimeoutTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override Config? Config => ConfigurationFactory.ParseString("""
        akka.test.default-timeout = 5s
        akka.scheduler.implementation = "Akka.TestKit.TestScheduler, Akka.TestKit"
        """);

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    private void AdvanceScheduler(TimeSpan offset) =>
        ((Akka.TestKit.TestScheduler)Sys.Scheduler).Advance(offset);

    [Fact]
    public async Task Ready_timeout_during_auto_retry_keeps_recovering_instead_of_zombieing()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeTransport
        {
            ConnectionState = ConnectionState.Disconnected,
            CurrentUserId = 42
        };
        var sink = new RecordingSink();
        var actor = Sys.ActorOf(DiscordNetGatewayLifecycleActor.CreateProps(
            transport, TimeProvider.System, sink, NullLogger.Instance));

        // 1. Caller-driven connect reaches READY.
        var connectTask = actor.Ask<DiscordGatewaySnapshot>(
            new DiscordNetGatewayLifecycleActor.Connect("test-token"),
            TimeSpan.FromSeconds(3), ct);
        await AwaitAssertAsync(() => Assert.Equal(1, transport.StartCount), cancellationToken: ct);
        transport.ConnectionState = ConnectionState.Connected;
        await transport.RaiseReadyAsync();
        Assert.True((await connectTask).IsReady);

        // 2. Spurious Connected while Ready forces clean reconnect cycle #1:
        //    publish, stop, and a zero-delay auto-retry is scheduled.
        await transport.RaiseConnectedAsync();
        await AwaitCleanReconnectSettledAsync(actor, ct);
        Assert.Equal(1, sink.CleanReconnectCount);

        // 3. Fire the auto-retry: the actor reconnects with no pending caller
        //    (ActorRefs.Nobody) and waits for READY.
        AdvanceScheduler(TimeSpan.FromSeconds(1));
        await AwaitAssertAsync(() => Assert.Equal(2, transport.StartCount), cancellationToken: ct);

        // 4. READY never arrives; advance past the 30s READY timeout. The
        //    regression: Nobody passed the pending-caller null check, so the
        //    actor silently entered CleanReconnectRequired and never emitted
        //    a second clean-reconnect request (and never recovered).
        AdvanceScheduler(TimeSpan.FromSeconds(31));
        await AwaitCleanReconnectSettledAsync(actor, ct);
        Assert.Equal(2, sink.CleanReconnectCount);

        // 5. The next auto-retry fires; this time READY arrives. The actor
        //    must fully recover AND publish ConnectionRestored — the same
        //    Nobody-vs-null skew kept the isRetry branch from ever firing.
        AdvanceScheduler(TimeSpan.FromSeconds(1));
        await AwaitAssertAsync(() => Assert.Equal(3, transport.StartCount), cancellationToken: ct);
        transport.ConnectionState = ConnectionState.Connected;
        await transport.RaiseReadyAsync();
        await AwaitAssertAsync(async () =>
        {
            var snapshot = await actor.Ask<DiscordGatewaySnapshot>(
                DiscordNetGatewayLifecycleActor.GetSnapshot.Instance, TimeSpan.FromSeconds(3), ct);
            Assert.True(snapshot.IsReady);
            Assert.Equal(1, sink.ConnectionRestoredCount);
        }, cancellationToken: ct);
    }

    /// <summary>
    /// Settles a clean-reconnect cycle before advancing virtual time. The
    /// "disconnected." health detail is written by the same stop-succeeded
    /// handler that arms the auto-retry timer, so once it is observable the
    /// timer is guaranteed to be scheduled — advancing earlier would tick
    /// virtual time past a timer that does not exist yet and stall the test.
    /// </summary>
    private async Task AwaitCleanReconnectSettledAsync(IActorRef actor, CancellationToken ct)
    {
        await AwaitAssertAsync(async () =>
        {
            var snapshot = await actor.Ask<DiscordGatewaySnapshot>(
                DiscordNetGatewayLifecycleActor.GetSnapshot.Instance, TimeSpan.FromSeconds(3), ct);
            Assert.False(snapshot.IsConnected);
            Assert.Equal("Discord gateway disconnected.", snapshot.HealthDetail);
        }, cancellationToken: ct);
    }

    private sealed class RecordingSink : IDiscordGatewayEventSink
    {
        public int CleanReconnectCount { get; private set; }

        public int ConnectionRestoredCount { get; private set; }

        public Task PublishMessageAsync(DiscordGatewayMessage message) => Task.CompletedTask;

        public Task PublishInteractionAsync(DiscordGatewayInteraction interaction) => Task.CompletedTask;

        public Task PublishCleanReconnectRequiredAsync(string reason)
        {
            CleanReconnectCount++;
            return Task.CompletedTask;
        }

        public Task PublishConnectionRestoredAsync(DiscordGatewaySnapshot snapshot)
        {
            ConnectionRestoredCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransport : IDiscordGatewayTransport
    {
        private Func<Task>? _connected;
        private Func<Task>? _ready;

        public event Func<LogMessage, Task> Log
        {
            add { }
            remove { }
        }

        public event Func<Task> Connected
        {
            add => _connected += value;
            remove => _connected -= value;
        }

        public event Func<Task> Ready
        {
            add => _ready += value;
            remove => _ready -= value;
        }

        public event Func<Exception, Task> Disconnected
        {
            add { }
            remove { }
        }

        public event Func<SocketMessage, Task> MessageReceived
        {
            add { }
            remove { }
        }

        public event Func<SocketMessageComponent, Task> ButtonExecuted
        {
            add { }
            remove { }
        }

        public ConnectionState ConnectionState { get; set; }

        public ulong? CurrentUserId { get; set; }

        public int StartCount { get; private set; }

        public Task LoginAsync(string botToken) => Task.CompletedTask;

        public Task StartAsync()
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            ConnectionState = ConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task LogoutAsync() => Task.CompletedTask;

        public Task RaiseConnectedAsync() => _connected?.Invoke() ?? Task.CompletedTask;

        public Task RaiseReadyAsync() => _ready?.Invoke() ?? Task.CompletedTask;
    }
}
