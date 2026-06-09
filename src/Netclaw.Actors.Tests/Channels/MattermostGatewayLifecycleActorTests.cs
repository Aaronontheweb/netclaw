// -----------------------------------------------------------------------
// <copyright file="MattermostGatewayLifecycleActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Pattern;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Transport;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostGatewayLifecycleActorTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 5s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Not_ready_ingress_is_dropped()
    {
        var transport = new FakeMattermostGatewayTransport();
        var sink = new RecordingGatewayEventSink();
        var actor = CreateLifecycleActor(transport, sink);
        await WaitForActorReadyAsync(actor);

        await transport.RaiseMessageAsync(CreateMessage("event-1"));
        await WaitForActorReadyAsync(actor);

        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task Runtime_disconnect_reports_not_ready_and_requests_clean_reconnect()
    {
        var transport = new FakeMattermostGatewayTransport();
        var sink = new RecordingGatewayEventSink();
        var actor = CreateLifecycleActor(transport, sink);

        var readySnapshot = await ConnectAsync(actor);
        Assert.True(readySnapshot.IsReady);

        await transport.RaiseDisconnectedAsync("network lost");

        // The actor detects the runtime disconnect, emits CleanReconnectRequired,
        // then drives a full stop/reconnect cycle. After the cycle completes the
        // actor lands back in Disconnected (not ready, not connected) with
        // auto-reconnect scheduled by the lifecycle actor.
        await AwaitAssertAsync(async () =>
        {
            var snapshot = await actor.Ask<MattermostGatewaySnapshot>(
                MattermostNetGatewayLifecycleActor.GetSnapshot.Instance,
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);

            Assert.False(snapshot.IsReady);
            Assert.False(snapshot.IsConnected);
            Assert.Equal(1, sink.CleanReconnectCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Connected_event_while_disconnected_requires_clean_reconnect()
    {
        var transport = new FakeMattermostGatewayTransport();
        var sink = new RecordingGatewayEventSink();
        var actor = CreateLifecycleActor(transport, sink);
        await WaitForActorReadyAsync(actor);

        transport.IsConnected = true;
        await transport.RaiseConnectedAsync();

        // The actor detects a spurious Connected event while in Disconnected
        // state and forces a clean reconnect cycle: it emits the
        // CleanReconnectRequired event, then drives a full stop/reconnect.
        // The fake transport's StopAsync sets IsConnected = false, so the
        // actor ends up back in Disconnected with IsConnected = false.
        await AwaitAssertAsync(async () =>
        {
            var snapshot = await actor.Ask<MattermostGatewaySnapshot>(
                MattermostNetGatewayLifecycleActor.GetSnapshot.Instance,
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);

            Assert.False(snapshot.IsReady);
            Assert.False(snapshot.IsConnected);
            Assert.Equal(1, sink.CleanReconnectCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reconnect_cycle_does_not_duplicate_transport_handlers()
    {
        var transport = new FakeMattermostGatewayTransport();
        var sink = new RecordingGatewayEventSink();
        var actor = CreateLifecycleActor(transport, sink);
        await WaitForActorReadyAsync(actor);

        AssertSingleSubscription(transport);

        await ConnectAsync(actor);
        await actor.Ask<MattermostGatewaySnapshot>(
            MattermostNetGatewayLifecycleActor.Disconnect.Instance,
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
        await ConnectAsync(actor);

        AssertSingleSubscription(transport);

        await transport.RaiseMessageAsync(CreateMessage("event-1"));
        await WaitForActorReadyAsync(actor);

        Assert.Single(sink.Messages);

        Sys.Stop(actor);
        await AwaitAssertAsync(
            () =>
            {
                Assert.Equal(0, transport.MessageSubscriberCount);
                Assert.Equal(0, transport.ConnectedSubscriberCount);
                Assert.Equal(0, transport.DisconnectedSubscriberCount);
                Assert.Equal(0, transport.LogSubscriberCount);
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private IActorRef CreateLifecycleActor(
        FakeMattermostGatewayTransport transport,
        RecordingGatewayEventSink sink)
    {
        return Sys.ActorOf(MattermostNetGatewayLifecycleActor.CreateProps(
            transport,
            sink,
            NullLogger.Instance));
    }

    private static async Task WaitForActorReadyAsync(IActorRef actor)
    {
        await actor.Ask<MattermostGatewaySnapshot>(
            MattermostNetGatewayLifecycleActor.GetSnapshot.Instance,
            TimeSpan.FromSeconds(3));
    }

    private static Task<MattermostGatewaySnapshot> ConnectAsync(IActorRef actor) =>
        actor.Ask<MattermostGatewaySnapshot>(
            new MattermostNetGatewayLifecycleActor.Connect("https://mattermost.test", "test-token"),
            TimeSpan.FromSeconds(3));

    private static void AssertSingleSubscription(FakeMattermostGatewayTransport transport)
    {
        Assert.Equal(1, transport.MessageSubscriberCount);
        Assert.Equal(1, transport.ConnectedSubscriberCount);
        Assert.Equal(1, transport.DisconnectedSubscriberCount);
        Assert.Equal(1, transport.LogSubscriberCount);
    }

    private static MattermostGatewayMessage CreateMessage(string eventId) =>
        new(
            EventId: new MattermostEventId(eventId),
            ChannelId: new MattermostChannelId("ch-1"),
            PostId: new MattermostPostId("post-" + eventId),
            RootPostId: new MattermostRootPostId("root-1"),
            SenderId: new MattermostUserId("user-1"),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: true,
            Text: "hello",
            ReceivedAt: DateTimeOffset.Parse("2026-06-08T00:00:00Z"));

    private sealed class RecordingGatewayEventSink : IMattermostGatewayEventSink
    {
        private readonly List<MattermostGatewayMessage> _messages = [];

        public IReadOnlyList<MattermostGatewayMessage> Messages => _messages;

        public int CleanReconnectCount { get; private set; }

        public Task PublishMessageAsync(MattermostGatewayMessage message)
        {
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task PublishCleanReconnectRequiredAsync(string reason)
        {
            CleanReconnectCount++;
            return Task.CompletedTask;
        }

        public int ConnectionRestoredCount { get; private set; }

        public Task PublishConnectionRestoredAsync(MattermostGatewaySnapshot snapshot)
        {
            ConnectionRestoredCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMattermostGatewayTransport : IMattermostGatewayTransport
    {
        private Func<MattermostGatewayMessage, Task>? _messageReceived;
        private Func<Task>? _connected;
        private Func<MattermostGatewayDisconnect, Task>? _disconnected;
        private Func<string, Task>? _logReceived;

        public event Func<MattermostGatewayMessage, Task> MessageReceived
        {
            add
            {
                _messageReceived += value;
                MessageSubscriberCount++;
            }
            remove
            {
                _messageReceived -= value;
                MessageSubscriberCount--;
            }
        }

        public event Func<Task> Connected
        {
            add
            {
                _connected += value;
                ConnectedSubscriberCount++;
            }
            remove
            {
                _connected -= value;
                ConnectedSubscriberCount--;
            }
        }

        public event Func<MattermostGatewayDisconnect, Task> Disconnected
        {
            add
            {
                _disconnected += value;
                DisconnectedSubscriberCount++;
            }
            remove
            {
                _disconnected -= value;
                DisconnectedSubscriberCount--;
            }
        }

        public event Func<string, Task> LogReceived
        {
            add
            {
                _logReceived += value;
                LogSubscriberCount++;
            }
            remove
            {
                _logReceived -= value;
                LogSubscriberCount--;
            }
        }

        public bool IsConnected { get; set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int MessageSubscriberCount { get; private set; }

        public int ConnectedSubscriberCount { get; private set; }

        public int DisconnectedSubscriberCount { get; private set; }

        public int LogSubscriberCount { get; private set; }

        public Task<MattermostBotIdentity> StartAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default)
        {
            StartCount++;
            IsConnected = true;
            return Task.FromResult(new MattermostBotIdentity("bot-1", "netclaw"));
        }

        public Task StopAsync()
        {
            StopCount++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task RaiseMessageAsync(MattermostGatewayMessage message) =>
            _messageReceived?.Invoke(message) ?? Task.CompletedTask;

        public Task RaiseConnectedAsync() =>
            _connected?.Invoke() ?? Task.CompletedTask;

        public Task RaiseDisconnectedAsync(string reason)
        {
            IsConnected = false;
            return _disconnected?.Invoke(new MattermostGatewayDisconnect(reason)) ?? Task.CompletedTask;
        }

        public Task RaiseLogAsync(string message) =>
            _logReceived?.Invoke(message) ?? Task.CompletedTask;
    }
}
