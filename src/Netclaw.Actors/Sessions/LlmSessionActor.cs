using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.PubSub;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Per-session actor managing LLM conversation state.
/// Receives <see cref="SendUserMessage"/>, invokes LLM, persists turns,
/// and publishes <see cref="TurnBroadcast"/> for adapters.
///
/// Stub — persistence, IChatClient integration, and compaction
/// implemented in subsequent tasks.
/// </summary>
public sealed class LlmSessionActor : ReceiveActor
{
    private readonly SessionId _sessionId;
    private readonly IActorRef _pubSub;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private readonly List<SerializableChatMessage> _history = new();

    public LlmSessionActor(string entityId, IRequiredActor<PubSubMediatorActor> pubSub)
    {
        _sessionId = new SessionId(entityId);
        _pubSub = pubSub.ActorRef;

        Receive<SendUserMessage>(OnSendUserMessage);
    }

    private void OnSendUserMessage(SendUserMessage cmd)
    {
        _log.Info("Session {0}: received message from {1}",
            _sessionId, cmd.Source.SenderIdentity);

        // Record user message in history
        var userMsg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = cmd.Content
        };
        _history.Add(userMsg);

        // TODO(Task 1.2): invoke IChatClient, persist TurnRecorded
        // For now, acknowledge receipt
        var reply = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "[stub] Message received, LLM integration pending."
        };
        _history.Add(reply);

        // Publish turn broadcast for adapters
        _pubSub.Tell(new Publish(_sessionId.Value, new TurnBroadcast
        {
            SessionId = _sessionId,
            AssistantReply = reply,
            BroadcastAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }));
    }
}
