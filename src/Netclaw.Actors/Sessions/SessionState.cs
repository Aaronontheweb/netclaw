using System.Collections.Immutable;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Immutable conversation state for an LLM session. Decoupled from the actor
/// so that state transitions (event application) are pure functions testable
/// without an ActorSystem.
///
/// The actor holds a single <c>SessionState</c> field and replaces it on each
/// event via the <c>Apply</c> methods. Transient concerns (subscribers, message
/// buffer, behavior) remain on the actor.
/// </summary>
public sealed record SessionState
{
    public static readonly SessionState Empty = new();

    public ImmutableList<SerializableChatMessage> History { get; init; } =
        ImmutableList<SerializableChatMessage>.Empty;

    public int TurnCount { get; init; }

    public string? Title { get; init; }

    // ── Event application (pure functions) ──

    public SessionState Apply(SystemPromptSet evt)
    {
        var systemMsg = new SerializableChatMessage
        {
            Role = ChatRole.System,
            Content = evt.Content
        };

        // System prompt is always the first message. Replace if present.
        if (History.Count > 0 && History[0].Role == ChatRole.System)
        {
            return this with { History = History.SetItem(0, systemMsg) };
        }

        return this with { History = History.Insert(0, systemMsg) };
    }

    public SessionState Apply(TurnRecorded evt)
    {
        return this with
        {
            History = History.Add(evt.UserMessage).Add(evt.AssistantReply),
            TurnCount = TurnCount + 1
        };
    }

    public SessionState Apply(SessionTitleSet evt)
    {
        return this with { Title = evt.Title };
    }

    public SessionState Apply(SessionCompacted evt)
    {
        // Preserve system prompt if present
        var builder = ImmutableList.CreateBuilder<SerializableChatMessage>();
        if (History.Count > 0 && History[0].Role == ChatRole.System)
        {
            builder.Add(History[0]);
        }

        builder.AddRange(evt.CompactedMessages);
        return this with { History = builder.ToImmutable() };
    }

    // ── Command helpers ──

    /// <summary>
    /// Add a user message to history (before firing an LLM call).
    /// This is transient state that gets persisted as part of <see cref="TurnRecorded"/>.
    /// </summary>
    public SessionState AddUserMessage(string content)
    {
        return this with
        {
            History = History.Add(new SerializableChatMessage
            {
                Role = ChatRole.User,
                Content = content
            })
        };
    }

    /// <summary>
    /// Add an error reply to history when an LLM call fails.
    /// </summary>
    public SessionState AddErrorReply(string errorMessage)
    {
        return this with
        {
            History = History.Add(new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                Content = errorMessage
            }),
            TurnCount = TurnCount + 1
        };
    }

    /// <summary>
    /// Find the last user message in history (for building persistence events).
    /// </summary>
    public SerializableChatMessage? FindLastUserMessage()
    {
        for (var i = History.Count - 1; i >= 0; i--)
        {
            if (History[i].Role == ChatRole.User)
                return History[i];
        }

        return null;
    }

    // ── Snapshot conversion ──

    public SessionSnapshot ToSnapshot()
    {
        return new SessionSnapshot
        {
            History = new List<SerializableChatMessage>(History),
            TurnCount = TurnCount,
            Title = Title
        };
    }

    public static SessionState FromSnapshot(SessionSnapshot snapshot)
    {
        return new SessionState
        {
            History = ImmutableList.CreateRange(snapshot.History),
            TurnCount = snapshot.TurnCount,
            Title = snapshot.Title
        };
    }
}
