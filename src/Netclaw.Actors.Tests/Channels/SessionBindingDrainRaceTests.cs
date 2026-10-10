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
using Netclaw.Channels.Mattermost;
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
                AllowedUserIds = ["USER-DRAIN"],
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
            SlackMessage(parent, channelId, threadTs, "denied-event", "denied", "USER-DENIED"),
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
                AllowedChannelIds = [channelId.Value],
                AllowedUserIds = ["USER-DRAIN"]
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
            DiscordMessage(parent, channelId, replyChannelId, threadId, "denied-event", "denied", "USER-DENIED"),
            ChannelType.Discord);
    }

    [Fact]
    public async Task Mattermost_parent_retries_input_after_binding_drain_finishes()
    {
        var pipeline = new GatedSessionPipeline();
        var channelId = new MattermostChannelId("CH-MATTER-DRAIN");
        var rootPostId = new MattermostRootPostId("root-drain");
        var sessionId = SessionIdFormat.Build(channelId.Value, rootPostId.Value);
        var replyClient = new RecordingMattermostReplyClient();
        var dependencies = new MattermostGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: new MattermostChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowedChannelIds = [channelId.Value],
                AllowedUserIds = ["USER-DRAIN"]
            },
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.MattermostWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultTextOnlyModel,
            StorageResolver: TestSessionStorageResolver.Instance,
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
        var parent = Sys.ActorOf(MattermostConversationActor.CreateProps(channelId, dependencies));

        await RunDrainRaceAsync(
            pipeline,
            parent,
            sessionId,
            $"{channelId.Value}:{rootPostId.Value}",
            MattermostMessage(parent, channelId, rootPostId, "initial-event", "initial"),
            MattermostMessage(parent, channelId, rootPostId, "raced-event", "raced"),
            MattermostMessage(parent, channelId, rootPostId, "denied-event", "denied", "USER-DENIED"),
            ChannelType.Mattermost);
    }

    private async Task RunDrainRaceAsync(
        GatedSessionPipeline pipeline,
        IActorRef parent,
        SessionId sessionId,
        string bindingKey,
        Action sendInitial,
        Action sendRaced,
        Action sendDenied,
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
            sendDenied();
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
            Assert.Equal(PrincipalClassification.TrustedInternal, retriedInput.Principal);
            Assert.Equal(initialInput.Provenance, retriedInput.Provenance);
            Assert.Equal("raced", Assert.IsType<TextContent>(Assert.Single(retriedInput.Contents)).Text);
            Assert.Equal(1, pipeline.Inputs.Count(input => input.MessageId == "raced-event"));
            Assert.DoesNotContain(pipeline.Inputs, input => input.MessageId == "denied-event");

            parent.Tell(new DeliverTrustedSessionTurn(
                SessionId: sessionId,
                Content: "completion barrier",
                Source: TrustedSource(channelType, "barrier-event")), TestActor);
            var barrierInput = await pipeline.InputAsync(1, "barrier-event").WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal("completion barrier", Assert.IsType<TextContent>(Assert.Single(barrierInput.Contents)).Text);
            Assert.Equal(TestActor, barrierInput.AckTarget);
            Assert.Equal(1, pipeline.Inputs.Count(input => input.MessageId == "raced-event"));
            Assert.DoesNotContain(pipeline.Inputs, input => input.MessageId == "denied-event");
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
        string text,
        string senderId = "USER-DRAIN") => () => parent.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId(eventId),
            ChannelId: channelId,
            ThreadTs: threadTs,
            EventTs: new SlackEventTs(eventId == "initial-event" ? "1000.2" : "1000.3"),
            UserId: new SlackUserId(senderId),
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
        string text,
        string senderId = "USER-DRAIN") => () => parent.Tell(new DiscordGatewayMessage(
            EventId: new DiscordEventId(eventId),
            ChannelId: channelId,
            ReplyChannelId: replyChannelId,
            MessageId: new DiscordMessageId(eventId),
            ThreadOrMessageId: threadId,
            RootMessageId: null,
            SenderId: new DiscordUserId(senderId),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: true,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow()), TestActor);

    private Action MattermostMessage(
        IActorRef parent,
        MattermostChannelId channelId,
        MattermostRootPostId rootPostId,
        string eventId,
        string text,
        string senderId = "USER-DRAIN") => () => parent.Tell(new MattermostGatewayMessage(
            EventId: new MattermostEventId(eventId),
            ChannelId: channelId,
            PostId: new MattermostPostId($"post-{eventId}"),
            RootPostId: rootPostId,
            SenderId: new MattermostUserId(senderId),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: false,
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

    [Fact]
    public async Task Slack_binding_returns_queued_delivery_before_retire_ready()
    {
        var pipeline = new GatedSessionPipeline();
        var channelId = new SlackChannelId("C-PRE-BARRIER");
        var threadTs = new SlackThreadTs("2000.1");
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
        var replyTo = CreateTestProbe("pre-barrier-reply-to");
        var observer = CreateTestProbe("pre-barrier-parent-events");
        var initial = new SlackThreadInbound(
            SessionId: sessionId,
            ChannelId: channelId,
            ThreadTs: threadTs,
            EventId: new SlackEventId("initial-before-retire"),
            TurnId: new TurnId("initial-turn"),
            SenderId: new SenderId("USER-PRE-BARRIER"),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new SourceKind("slack")
            },
            Text: "Initial input",
            ReceivedAt: TimeProvider.System.GetUtcNow());
        var queued = initial with
        {
            EventId = new SlackEventId("queued-before-retire-barrier"),
            TurnId = new TurnId("queued-turn"),
            Text = "Must return to the parent"
        };
        var bindingProps = SlackThreadBindingActor.CreateProps(sessionId, channelId, threadTs, dependencies);
        var parent = Sys.ActorOf(Props.Create(() => new RetireBarrierHarnessActor(
            bindingProps,
            observer.Ref,
            queued,
            replyTo.Ref)));

        try
        {
            parent.Tell(StartRealBinding.Instance, TestActor);
            var binding = await observer.ExpectMsgAsync<RealBindingStarted>(
                TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
            var firstInput = await pipeline.FirstInputAsync(0).WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal("initial-before-retire", firstInput.MessageId);

            var outputRef = await pipeline.FirstOutputActor.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            await pipeline.DrainGateReady.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);
            outputRef.Tell(new SessionDeactivated { SessionId = sessionId });

            await observer.ExpectMsgAsync<RetirementAndBarrierQueued>(
                TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
            await pipeline.FirstInputCompleted.Task.WaitAsync(
                TestTimeout, TestContext.Current.CancellationToken);

            pipeline.ReleaseDrainGate();
            var returned = await observer.ExpectMsgAsync<DeferredDeliveryObserved>(
                TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
            var ready = await observer.ExpectMsgAsync<RetireReadyObserved>(
                TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(binding.Binding, returned.Binding);
            Assert.Same(queued, Assert.IsType<SlackThreadInbound>(returned.Delivery.Message));
            Assert.Equal(replyTo.Ref, returned.Delivery.ReplyTo);
            Assert.Equal(binding.Binding, ready.Binding);
            Assert.DoesNotContain(pipeline.Inputs, input => input.MessageId == "queued-before-retire-barrier");
            await observer.ExpectMsgAsync<RealBindingStopped>(
                TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            pipeline.ReleaseDrainGate();
        }
    }

    private sealed class RetireBarrierHarnessActor : ReceiveActor
    {
        private readonly Props _bindingProps;
        private readonly IActorRef _observer;
        private readonly SlackThreadInbound _queued;
        private readonly IActorRef _replyTo;
        private IActorRef? _binding;

        public RetireBarrierHarnessActor(
            Props bindingProps,
            IActorRef observer,
            SlackThreadInbound queued,
            IActorRef replyTo)
        {
            _bindingProps = bindingProps;
            _observer = observer;
            _queued = queued;
            _replyTo = replyTo;

            Receive<StartRealBinding>(_ =>
            {
                var initial = _queued with
                {
                    EventId = new SlackEventId("initial-before-retire"),
                    TurnId = new TurnId("initial-turn"),
                    Text = "Initial input"
                };
                _binding = Context.ActorOf(_bindingProps, "real-binding");
                Context.Watch(_binding);
                _observer.Tell(new RealBindingStarted(_binding), Self);
                _binding.Tell(new SessionBindingDelivery(initial, _replyTo), Self);
            });

            Receive<SessionBindingRetiring>(_ =>
            {
                if (Sender != _binding)
                    throw new InvalidOperationException("Only the real binding can start retirement.");

                _binding!.Tell(new SessionBindingDelivery(_queued, _replyTo), Self);
                _binding.Tell(SessionBindingRetireBarrier.Instance, Self);
                _observer.Tell(RetirementAndBarrierQueued.Instance, Self);
            });

            Receive<SessionBindingDelivery>(delivery =>
                _observer.Tell(new DeferredDeliveryObserved(Sender, delivery), Self));

            Receive<SessionBindingRetireReady>(_ =>
            {
                _observer.Tell(new RetireReadyObserved(Sender), Self);
                if (Sender == _binding)
                    Context.Stop(Sender);
            });

            Receive<Terminated>(terminated =>
                _observer.Tell(new RealBindingStopped(terminated.ActorRef), Self));
        }
    }

    private sealed record StartRealBinding
    {
        public static readonly StartRealBinding Instance = new();
    }
    private sealed record RealBindingStarted(IActorRef Binding);
    private sealed record RetirementAndBarrierQueued
    {
        public static readonly RetirementAndBarrierQueued Instance = new();
    }
    private sealed record DeferredDeliveryObserved(IActorRef Binding, SessionBindingDelivery Delivery);
    private sealed record RetireReadyObserved(IActorRef Binding);
    private sealed record RealBindingStopped(IActorRef Binding);

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
