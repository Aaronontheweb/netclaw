using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Per-session persistent actor managing LLM conversation state.
/// Receives <see cref="SendUserMessage"/>, invokes <see cref="IChatClient"/>,
/// persists <see cref="TurnRecorded"/> events, and sends strongly-typed
/// <see cref="SessionOutput"/> events to subscribers filtered by <see cref="OutputFilter"/>.
///
/// Conversation state is held in an immutable <see cref="SessionState"/> record.
/// The actor owns only transient concerns: subscribers, message buffer, and behavior.
///
/// Uses two command behaviors:
/// - Ready: accepts user messages and fires async LLM call
/// - Processing: buffers incoming messages while LLM call is in flight
/// </summary>
public sealed class LlmSessionActor : ReceivePersistentActor
{
    private readonly SessionId _sessionId;
    private readonly IChatClient _chatClient;
    private readonly SessionConfig _config;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    // Transient state (not persisted)
    private readonly List<SendUserMessage> _buffer = new();
    private readonly Dictionary<IActorRef, OutputFilter> _subscribers = new();

    // Persistent state (immutable — replaced on each event)
    private SessionState _state = SessionState.Empty;

    public override string PersistenceId { get; }

    public LlmSessionActor(string entityId, IChatClient chatClient, SessionConfig config)
    {
        _sessionId = new SessionId(entityId);
        _chatClient = chatClient;
        _config = config;
        PersistenceId = $"session-{entityId}";

        // ── Recovery handlers ──
        Recover<SystemPromptSet>(evt => _state = _state.Apply(evt));
        Recover<TurnRecorded>(evt => _state = _state.Apply(evt));
        Recover<SessionTitleSet>(evt => _state = _state.Apply(evt));
        Recover<SessionCompacted>(evt => _state = _state.Apply(evt));
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is SessionSnapshot snapshot)
            {
                _state = SessionState.FromSnapshot(snapshot);
                _log.Info("Session {0}: recovered from snapshot (turns={1})",
                    _sessionId, _state.TurnCount);
            }
        });
        Recover<RecoveryCompleted>(_ =>
        {
            _log.Info("Session {0}: recovery complete (turns={1}, history={2})",
                _sessionId, _state.TurnCount, _state.History.Count);
            Become(Ready);
        });
    }

    // ── Command behaviors ──

    private void Ready()
    {
        CommandSubscriptionMessages();
        CommandSnapshotMessages();

        Command<SendUserMessage>(cmd =>
        {
            _log.Info("Session {0}: received user message", _sessionId);

            _state = _state.AddUserMessage(cmd.Content);
            Sender.Tell(CommandAck.For(_sessionId));
            FireLlmCall();
            Become(Processing);
        });
    }

    private void Processing()
    {
        CommandSubscriptionMessages();
        CommandSnapshotMessages();

        Command<SendUserMessage>(cmd =>
        {
            _log.Info("Session {0}: buffering user message (LLM call in progress)", _sessionId);
            _buffer.Add(cmd);
            Sender.Tell(CommandAck.For(_sessionId));
        });

        Command<LlmResponseReceived>(msg =>
        {
            var response = msg.Response;
            var lastMessage = response.Messages[^1];
            var reply = ChatMessageConverter.FromAiMessage(lastMessage);

            // Build the persistence event
            var userMsg = _state.FindLastUserMessage();

            var turnEvent = new TurnRecorded
            {
                SessionId = _sessionId,
                UserMessage = userMsg ?? new SerializableChatMessage
                {
                    Role = Protocol.ChatRole.User,
                    Content = string.Empty
                },
                AssistantReply = reply,
                RecordedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            Persist(turnEvent, evt =>
            {
                // Apply state: user message is already in history from AddUserMessage,
                // so we only add the assistant reply and increment turn count.
                _state = _state with
                {
                    History = _state.History.Add(evt.AssistantReply),
                    TurnCount = _state.TurnCount + 1
                };

                // Side effects: emit to subscribers
                EmitResponseOutputs(lastMessage, response.Usage);

                // Snapshot check
                MaybeSnapshot();

                // Drain buffer or return to Ready
                if (_buffer.Count > 0)
                {
                    _log.Info("Session {0}: draining {1} buffered message(s)",
                        _sessionId, _buffer.Count);
                    foreach (var buffered in _buffer)
                    {
                        _state = _state.AddUserMessage(buffered.Content);
                    }
                    _buffer.Clear();
                    FireLlmCall();
                }
                else
                {
                    Become(Ready);
                }
            });
        });

        Command<LlmCallFailed>(msg =>
        {
            _log.Error(msg.Cause, "Session {0}: LLM call failed", _sessionId);

            const string errorMessage = "I encountered an error processing your message. Please try again.";
            _state = _state.AddErrorReply(errorMessage);

            EmitOutput(new ErrorOutput
            {
                SessionId = _sessionId,
                Message = errorMessage
            });
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = _state.TurnCount
            });

            _buffer.Clear();
            Become(Ready);
        });
    }

    private void CommandSubscriptionMessages()
    {
        Command<JoinSession>(cmd =>
        {
            _subscribers[cmd.Subscriber] = cmd.Filter;
            Context.WatchWith(cmd.Subscriber,
                new LeaveSession { SessionId = _sessionId, Subscriber = cmd.Subscriber });

            _log.Info("Session {0}: {1} joined (filter={2})",
                _sessionId, cmd.Subscriber, cmd.Filter);

            cmd.Subscriber.Tell(new SessionJoined
            {
                SessionId = _sessionId,
                Title = _state.Title,
                TurnCount = _state.TurnCount
            });
        });

        Command<LeaveSession>(cmd =>
        {
            if (_subscribers.Remove(cmd.Subscriber))
            {
                _log.Info("Session {0}: {1} left", _sessionId, cmd.Subscriber);
            }
        });
    }

    private void CommandSnapshotMessages()
    {
        Command<SaveSnapshotSuccess>(msg =>
        {
            _log.Info("Session {0}: snapshot saved (seqNr={1})", _sessionId, msg.Metadata.SequenceNr);
        });

        Command<SaveSnapshotFailure>(msg =>
        {
            _log.Warning("Session {0}: snapshot failed: {1}", _sessionId, msg.Cause.Message);
        });
    }

    protected override void PreRestart(Exception reason, object message)
    {
        foreach (var buffered in _buffer)
        {
            Self.Tell(buffered);
        }
        _buffer.Clear();

        base.PreRestart(reason, message);
    }

    // ── Helpers ──

    private void FireLlmCall()
    {
        var messages = ChatMessageConverter.ToAiMessages(_state.History);
        var self = Self;
        var client = _chatClient;
        _ = InvokeLlmAsync(client, messages, self);
    }

    private static async Task InvokeLlmAsync(
        IChatClient client, List<AiChatMessage> messages, IActorRef self)
    {
        try
        {
            var response = await client.GetResponseAsync(messages);
            self.Tell(new LlmResponseReceived { Response = response });
        }
        catch (Exception ex)
        {
            self.Tell(new LlmCallFailed { Cause = ex });
        }
    }

    private void MaybeSnapshot()
    {
        if (_config.SnapshotInterval > 0 && LastSequenceNr % _config.SnapshotInterval == 0)
        {
            SaveSnapshot(_state.ToSnapshot());
        }
    }

    /// <summary>
    /// Decompose the MEAI ChatMessage into strongly-typed session output events.
    /// </summary>
    private void EmitResponseOutputs(AiChatMessage message, UsageDetails? usage)
    {
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    EmitOutput(new TextOutput
                    {
                        SessionId = _sessionId,
                        Text = text.Text ?? string.Empty
                    }, OutputFilter.Text);
                    break;

                case TextReasoningContent thinking:
                    EmitOutput(new ThinkingOutput
                    {
                        SessionId = _sessionId,
                        Text = thinking.Text ?? string.Empty
                    }, OutputFilter.Thinking);
                    break;

                case FunctionCallContent toolCall:
                    EmitOutput(new ToolCallOutput
                    {
                        SessionId = _sessionId,
                        CallId = toolCall.CallId,
                        ToolName = toolCall.Name,
                        ArgumentsJson = toolCall.Arguments is not null
                            ? JsonSerializer.Serialize(toolCall.Arguments)
                            : null
                    }, OutputFilter.ToolCalls);
                    break;
            }
        }

        if (usage is not null)
        {
            var contextWindow = _config.ContextWindowTokens;
            double? usagePercent = usage.InputTokenCount.HasValue && contextWindow > 0
                ? (double)usage.InputTokenCount.Value / contextWindow
                : null;

            EmitOutput(new UsageOutput
            {
                SessionId = _sessionId,
                InputTokens = usage.InputTokenCount,
                OutputTokens = usage.OutputTokenCount,
                TotalTokens = usage.TotalTokenCount,
                CachedInputTokens = usage.CachedInputTokenCount,
                ReasoningTokens = usage.ReasoningTokenCount,
                ContextWindowTokens = contextWindow,
                UsagePercent = usagePercent
            }, OutputFilter.Usage);
        }

        EmitOutput(new TurnCompleted
        {
            SessionId = _sessionId,
            TurnNumber = _state.TurnCount
        });
    }

    /// <summary>
    /// Send output to subscribers whose filter includes the required flag.
    /// Lifecycle messages pass <see cref="OutputFilter.None"/> (default) to reach all subscribers.
    /// </summary>
    private void EmitOutput(SessionOutput output, OutputFilter requiredFlag = OutputFilter.None)
    {
        foreach (var (subscriber, filter) in _subscribers)
        {
            if (requiredFlag == OutputFilter.None || filter.HasFlag(requiredFlag))
            {
                subscriber.Tell(output);
            }
        }
    }

    internal void SetTitle(string title)
    {
        var evt = new SessionTitleSet
        {
            SessionId = _sessionId,
            Title = title,
            SetAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        Persist(evt, e =>
        {
            _state = _state.Apply(e);
            EmitOutput(new SessionTitleOutput
            {
                SessionId = _sessionId,
                Title = title
            });
        });
    }
}
