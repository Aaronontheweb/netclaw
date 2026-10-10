// -----------------------------------------------------------------------
// <copyright file="SessionBindingDeactivationIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SessionBindingDeactivationIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _chatClient = new();

    public SessionBindingDeactivationIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new TestChatClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000
        });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.FromHours(1),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
    }

    [Fact]
    public async Task Slack_binding_stops_after_its_real_session_passivates()
    {
        var sessionId = new SessionId("channel-slack/thread-session-deactivation");
        var replyClient = new RecordingSlackReplyClient();
        var sessionObserver = CreateTestProbe("slack-session-output");
        await JoinSessionAsync(
            ActorRegistry.Get<SessionManagerActorKey>(),
            sessionObserver,
            sessionId,
            OutputFilter.Full);
        var dependencies = new SlackGatewayDependencies(
            Pipeline: Host.Services.GetRequiredService<ISessionPipeline>(),
            IngressGate: null,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                AllowDirectMessages = true,
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
            StorageResolver: new TestSessionStorageResolver(TestPaths),
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
        var binding = Sys.ActorOf(SlackThreadBindingActor.CreateProps(
            sessionId,
            new SlackChannelId("C-TEST"),
            new SlackThreadTs("1000.1"),
            dependencies));

        var bindingWatcher = CreateTestProbe("slack-binding-watch");
        bindingWatcher.Watch(binding);

        binding.Tell(new SlackThreadInbound(
            SessionId: sessionId,
            ChannelId: new SlackChannelId("C-TEST"),
            ThreadTs: new SlackThreadTs("1000.1"),
            EventId: new SlackEventId("slack-deactivation-event"),
            TurnId: new TurnId("slack-deactivation-turn"),
            SenderId: new SenderId("U-USER"),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new SourceKind("slack")
            },
            Text: "Reply before session shutdown",
            ReceivedAt: TimeProvider.System.GetUtcNow()), TestActor);

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(replyClient.Posts, post => post.Text.Contains(
                "Response #1", StringComparison.Ordinal));
        }, TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);
        await sessionObserver.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);

        var session = await ResolveSessionAsync(sessionId);
        var sessionWatcher = CreateTestProbe("slack-session-watch");
        sessionWatcher.Watch(session);
        session.Tell(ReceiveTimeout.Instance);

        await sessionObserver.ExpectMsgAsync<SessionDeactivated>(
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await sessionWatcher.ExpectTerminatedAsync(
            session,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await bindingWatcher.ExpectTerminatedAsync(
            binding,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Discord_binding_stops_after_its_real_session_passivates()
    {
        var sessionId = new SessionId("channel-discord/thread-session-deactivation");
        var replyClient = new SignalDiscordReplyClient();
        var sessionObserver = CreateTestProbe("discord-session-output");
        await JoinSessionAsync(
            ActorRegistry.Get<SessionManagerActorKey>(),
            sessionObserver,
            sessionId,
            OutputFilter.Full);
        var dependencies = new DiscordGatewayDependencies(
            Pipeline: Host.Services.GetRequiredService<ISessionPipeline>(),
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: new DiscordChannelOptions(),
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            StorageResolver: new TestSessionStorageResolver(TestPaths),
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
        var binding = Sys.ActorOf(DiscordSessionBindingActor.CreateProps(
            sessionId,
            new DiscordChannelId("CH-TEST"),
            new DiscordReplyChannelId("REPLY-TEST"),
            new DiscordThreadOrMessageId("THREAD-TEST"),
            rootMessageId: null,
            dependencies));

        var bindingWatcher = CreateTestProbe("discord-binding-watch");
        bindingWatcher.Watch(binding);

        binding.Tell(new DiscordThreadInbound(
            SessionId: sessionId,
            ChannelId: new DiscordChannelId("CH-TEST"),
            ReplyChannelId: new DiscordReplyChannelId("REPLY-TEST"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("THREAD-TEST"),
            RootMessageId: null,
            EventId: new DiscordEventId("discord-deactivation-event"),
            SenderId: new DiscordUserId("USER-TEST"),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new SourceKind("discord")
            },
            Text: "Reply before session shutdown",
            ReceivedAt: TimeProvider.System.GetUtcNow()), TestActor);

        var postedReply = await replyClient.FirstPost.Task.WaitAsync(
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Contains("Response #1", postedReply.Text, StringComparison.Ordinal);
        await sessionObserver.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);

        var session = await ResolveSessionAsync(sessionId);
        var sessionWatcher = CreateTestProbe("discord-session-watch");
        sessionWatcher.Watch(session);
        session.Tell(ReceiveTimeout.Instance);

        await sessionObserver.ExpectMsgAsync<SessionDeactivated>(
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await sessionWatcher.ExpectTerminatedAsync(
            session,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await bindingWatcher.ExpectTerminatedAsync(
            binding,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<IActorRef> ResolveSessionAsync(SessionId sessionId)
    {
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        return await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    private sealed class TestChatClientProvider(IChatClient client) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => client;
    }

    private sealed class SignalDiscordReplyClient : IDiscordReplyClient
    {
        public TaskCompletionSource<DiscordPostMessage> FirstPost { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DiscordPostResult> PostReplyAsync(
            DiscordPostMessage message,
            CancellationToken cancellationToken = default)
        {
            FirstPost.TrySetResult(message);
            return Task.FromResult(new DiscordPostResult(MessageId: new DiscordMessageId("message-test")));
        }

        public Task SetThreadNameAsync(
            DiscordReplyChannelId threadChannelId,
            string name,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateMessageAsync(
            DiscordReplyChannelId channelId,
            DiscordMessageId messageId,
            string text,
            bool removeComponents = false,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TriggerTypingAsync(
            DiscordReplyChannelId channelId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<DiscordMessageId?> UploadFileAsync(
            DiscordFileUpload upload,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DiscordMessageId?>(null);
    }
}
