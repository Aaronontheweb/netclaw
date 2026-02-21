using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.PubSub;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration test that exercises the full Netclaw actor pipeline:
/// message extraction → routing → session actor → pub/sub → subscriber delivery.
/// Uses production hosting extension methods with Akka.Hosting.TestKit.
/// </summary>
public class LlmSessionIntegrationTests : TestKit
{
    public LlmSessionIntegrationTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithNetclawActors();
    }

    [Fact]
    public void SendUserMessage_routes_through_session_and_publishes_TurnBroadcast()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/test-thread");
        var pubSub = ActorRegistry.Get<PubSubMediatorActor>();
        var sessionManager = ActorRegistry.Get<SessionManagerActor>();
        var subscriber = CreateTestProbe("adapter-probe");

        // Subscribe test probe to the session topic
        pubSub.Tell(new Subscribe(sessionId.Value, subscriber));

        // Act — send a user message through the session manager
        sessionManager.Tell(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, Netclaw!"
        });

        // Assert — subscriber receives TurnBroadcast via pub/sub
        var broadcast = subscriber.ExpectMsg<TurnBroadcast>(TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, broadcast.SessionId);
        Assert.Equal(ChatRole.Assistant, broadcast.AssistantReply.Role);
        Assert.NotEmpty(broadcast.AssistantReply.Content);
    }

    [Fact]
    public void Two_sessions_are_routed_independently()
    {
        // Arrange
        var session1 = new SessionId("channel-A/thread-1");
        var session2 = new SessionId("channel-B/thread-2");
        var pubSub = ActorRegistry.Get<PubSubMediatorActor>();
        var sessionManager = ActorRegistry.Get<SessionManagerActor>();
        var sub1 = CreateTestProbe("adapter-1");
        var sub2 = CreateTestProbe("adapter-2");

        pubSub.Tell(new Subscribe(session1.Value, sub1));
        pubSub.Tell(new Subscribe(session2.Value, sub2));

        // Act — send messages to both sessions
        sessionManager.Tell(new SendUserMessage
        {
            SessionId = session1,
            Content = "Message for session 1",
        });
        sessionManager.Tell(new SendUserMessage
        {
            SessionId = session2,
            Content = "Message for session 2"
        });

        // Assert — each subscriber only gets its own session's broadcast
        var broadcast1 = sub1.ExpectMsg<TurnBroadcast>(TimeSpan.FromSeconds(3));
        Assert.Equal(session1, broadcast1.SessionId);

        var broadcast2 = sub2.ExpectMsg<TurnBroadcast>(TimeSpan.FromSeconds(3));
        Assert.Equal(session2, broadcast2.SessionId);

        // Neither probe should receive extra messages
        sub1.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
        sub2.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }
}
