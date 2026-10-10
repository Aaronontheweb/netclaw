// -----------------------------------------------------------------------
// <copyright file="SessionBindingDrainRaceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka;
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SessionBindingDrainRaceTests(ITestOutputHelper output) : TestKit(output: output)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 5s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Slack_parent_retries_input_after_binding_drain_finishes()
    {
        var pipeline = new GatedSessionPipeline();
        var channelId = new SlackChannelId("C-DRAIN");
        var threadTs = new SlackThreadTs("1000.1");
        var sessionId = SessionIdFormat.Build(channelId.Value, threadTs.Value);
        var replyClient = new RecordingSlackReplyClient();
        var dependencies = new SlackGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowedChannelIds = [channelId.Value],
                BotToken = new SensitiveString("xoxb-test")
            },
            BotUserId: new SlackUserId("U-BOT"),
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.SlackWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            ThreadHistoryFetcher: EmptyThreadHistoryFetcher.Instance,
            AudienceProfiles: TestSlackGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestSlackGatewayDeps.DefaultTextOnlyModel,
            StorageResolver: TestSessionStorageResolver.Instance,
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
        var parent = Sys.ActorOf(SlackConversationActor.CreateProps(channelId, dependencies));

        await RunDrainRaceAsync(
            pipeline,
            parent,
            sessionId,
            threadTs.Value,
            SlackMessage(parent, channelId, threadTs, "initial-event", "initial"),
            SlackMessage(parent, channelId, threadTs, "raced-event", "raced"),
            ChannelType.Slack);
    }

    [Fact]
    public async Task Discord_parent_retries_input_after_binding_drain_finishes()
    {
        var pipeline = new GatedSessionPipeline();
        var channelId = new DiscordChannelId("CH-DRAIN");
        var threadId = new DiscordThreadOrMessageId("thread-drain");
        var replyChannelId = new DiscordReplyChannelId("CH-DRAIN");
        var sessionId = SessionIdFormat.Build(channelId.Value, threadId.Value);
        var replyClient = new RecordingDiscordReplyClient();
        var dependencies = new DiscordGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: new DiscordChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowedChannelIds = [channelId.Value]
            },
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            StorageResolver: TestSessionStorageResolver.Instance,
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
        var parent = Sys.ActorOf(DiscordConversationActor.CreateProps(channelId, dependencies));

        await RunDrainRaceAsync(
            pipeline,
            parent,
            sessionId,
            $"{channelId.Value}:{threadId.Value}",
            DiscordMessage(parent, channelId, replyChannelId, threadId, "initial-event", "initial"),
            DiscordMessage(parent, channelId, replyChannelId, threadId, "raced-event", "raced"),
            ChannelType.Discord);
    }

    private async Task RunDrainRaceAsync(
        GatedSessionPipeline pipeline,
        IActorRef parent,
        SessionId sessionId,
        string bindingKey,
        Action sendInitial,
        Action sendRaced,
        ChannelType channelType)
    {
        try
        {
            sendInitial();
            var initialInput = await pipeline.FirstInputAsync(0).WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal("initial-event", initialInput.MessageId);

            var outputRef = await pipeline.FirstOutputActor.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            await pipeline.DrainGateReady.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            var bindingName = Uri.EscapeDataString(bindingKey);
            var binding = await Sys.ActorSelection($"{parent.Path}/{bindingName}")
                .ResolveOne(TestTimeout, TestContext.Current.CancellationToken);
            var watcher = CreateTestProbe("binding-drain-watch");
            watcher.Watch(binding);

            outputRef.Tell(new SessionDeactivated { SessionId = sessionId });
            await pipeline.FirstInputCompleted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            sendRaced();
            parent.Tell(new DeliverTrustedSessionTurn(
                SessionId: new SessionId("WRONG/thread"),
                Content: "barrier",
                Source: TrustedSource(channelType)), TestActor);
            var nack = await ExpectMsgAsync<CommandNack>(
                TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("WRONG/thread", nack.SessionId.Value);

            pipeline.ReleaseDrainGate();
            await watcher.ExpectTerminatedAsync(
                binding,
                TestTimeout,
                cancellationToken: TestContext.Current.CancellationToken);

            var retriedInput = await pipeline.FirstInputAsync(1).WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal("raced-event", retriedInput.MessageId);
            Assert.Equal(sessionId.Value.Split('/')[0], retriedInput.ChannelId);
            Assert.Equal("USER-DRAIN", retriedInput.SenderId.Value);
            Assert.Equal(TrustAudience.Team, retriedInput.Audience);
            Assert.Equal(TrustBoundary.TrustedInstance, retriedInput.Boundary);
            Assert.Equal(PrincipalClassification.UntrustedExternal, retriedInput.Principal);
            Assert.Equal("raced", Assert.IsType<TextContent>(Assert.Single(retriedInput.Contents)).Text);
            Assert.Equal(1, pipeline.Inputs.Count(input => input.MessageId == "raced-event"));

            parent.Tell(new DeliverTrustedSessionTurn(
                SessionId: sessionId,
                Content: "completion barrier",
                Source: TrustedSource(channelType, "barrier-event")), TestActor);
            var ack = await ExpectMsgAsync<CommandAck>(
                TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(sessionId, ack.SessionId);
            var barrierInput = await pipeline.InputAsync(1, "barrier-event").WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal("completion barrier", Assert.IsType<TextContent>(Assert.Single(barrierInput.Contents)).Text);
            Assert.Equal(1, pipeline.Inputs.Count(input => input.MessageId == "raced-event"));
        }
        finally
        {
            pipeline.ReleaseDrainGate();
        }
    }

    private Action SlackMessage(
        IActorRef parent,
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        string eventId,
        string text) => () => parent.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId(eventId),
            ChannelId: channelId,
            ThreadTs: threadTs,
            EventTs: new SlackEventTs(eventId == "initial-event" ? "1000.2" : "1000.3"),
            UserId: new SlackUserId("USER-DRAIN"),
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false), TestActor);

    private Action DiscordMessage(
        IActorRef parent,
        DiscordChannelId channelId,
        DiscordReplyChannelId replyChannelId,
        DiscordThreadOrMessageId threadId,
        string eventId,
        string text) => () => parent.Tell(new DiscordGatewayMessage(
            EventId: new DiscordEventId(eventId),
            ChannelId: channelId,
            ReplyChannelId: replyChannelId,
            MessageId: new DiscordMessageId(eventId),
            ThreadOrMessageId: threadId,
            RootMessageId: null,
            SenderId: new DiscordUserId("USER-DRAIN"),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: true,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow()), TestActor);

    private static MessageSource TrustedSource(ChannelType channelType, string? messageId = null) => new()
    {
        ChannelType = channelType,
        SenderId = new SenderId("system"),
        MessageId = messageId,
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.TrustedInstance,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind("test")
        }
    };

    private sealed class GatedSessionPipeline : ISessionPipeline
    {
        private readonly ConcurrentDictionary<int, ConcurrentQueue<ChannelInput>> _inputs = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<ChannelInput>> _firstInputs = new();
        private readonly ConcurrentDictionary<(int Generation, string MessageId), TaskCompletionSource<ChannelInput>>
            _inputsByMessage = new();
        private readonly TaskCompletionSource<IActorRef> _firstOutputActor = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _generation;

        public TaskCompletionSource<bool> FirstInputCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DrainGateReady { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IActorRef> FirstOutputActor => _firstOutputActor;
        public IReadOnlyList<ChannelInput> Inputs => _inputs.Values.SelectMany(queue => queue).ToArray();

        public Task<ChannelInput> FirstInputAsync(int generation) =>
            _firstInputs.GetOrAdd(generation, _ => new TaskCompletionSource<ChannelInput>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public Task<ChannelInput> InputAsync(int generation, string messageId) =>
            _inputsByMessage.GetOrAdd((generation, messageId), _ => new TaskCompletionSource<ChannelInput>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public void ReleaseDrainGate() => _releaseGate.TrySetResult(true);

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            var generation = Interlocked.Increment(ref _generation) - 1;
            var queue = _inputs.GetOrAdd(generation, _ => new ConcurrentQueue<ChannelInput>());
            var input = Sink.ForEach<ChannelInput>(channelInput =>
                {
                    queue.Enqueue(channelInput);
                    _firstInputs.GetOrAdd(generation, _ => new TaskCompletionSource<ChannelInput>(
                        TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(channelInput);
                    if (channelInput.MessageId is { } messageId)
                        _inputsByMessage.GetOrAdd((generation, messageId), _ => new TaskCompletionSource<ChannelInput>(
                            TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(channelInput);
                })
                .MapMaterializedValue(completion =>
                {
                    if (generation == 0)
                    {
                        _ = completion.ContinueWith(
                            task =>
                            {
                                if (task.IsCompletedSuccessfully)
                                    FirstInputCompleted.TrySetResult(true);
                                else if (task.IsCanceled)
                                    FirstInputCompleted.TrySetCanceled();
                                else if (task.Exception is { } exception)
                                    FirstInputCompleted.TrySetException(exception);
                                else
                                    FirstInputCompleted.TrySetException(
                                        new InvalidOperationException("Input stream failed."));
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }

                    return NotUsed.Instance;
                });

            var killSwitch = KillSwitches.Shared($"binding-drain-test-{sessionId.Value}");
            var actorSource = Source.ActorRef<SessionOutput>(16, OverflowStrategy.DropHead)
                .Via(killSwitch.Flow<SessionOutput>());
            Source<SessionOutput, IActorRef> output = actorSource;
            if (generation == 0)
            {
                output = actorSource.ConcatMaterialized(
                    Source.FromTask(DrainSentinelAsync(sessionId)),
                    (actorRef, _) => actorRef);
            }

            var mappedOutput = output.MapMaterializedValue(actorRef =>
            {
                if (generation == 0)
                {
                    _firstOutputActor.TrySetResult(actorRef);
                    DrainGateReady.TrySetResult(true);
                }

                return NotUsed.Instance;
            });

            return Task.FromResult(new MaterializedSession(input, mappedOutput, killSwitch));
        }

        private async Task<SessionOutput> DrainSentinelAsync(SessionId sessionId)
        {
            await _releaseGate.Task;
            return new SessionDeactivated { SessionId = sessionId };
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(
            IWithSessionId feedback,
            CancellationToken ct = default) =>
            Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }
}
