using ProtoBuf;

namespace Akka.Agents.Protocol;

/// <summary>
/// Persisted event recording a completed turn (user message + assistant reply).
/// This is the primary persistence unit for session state recovery.
/// </summary>
[ProtoContract]
public sealed class TurnRecorded
{
    /// <summary>Entity key of the session that produced this turn.</summary>
    [ProtoMember(1)]
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>The user message that initiated the turn.</summary>
    [ProtoMember(2)]
    public SerializableChatMessage UserMessage { get; set; } = new();

    /// <summary>The assistant reply produced during the turn.</summary>
    [ProtoMember(3)]
    public SerializableChatMessage AssistantReply { get; set; } = new();

    /// <summary>Timestamp when this turn was recorded (Unix milliseconds, UTC).</summary>
    [ProtoMember(4)]
    public long RecordedAtMs { get; set; }

    /// <summary>Returns the recorded-at time as a DateTimeOffset.</summary>
    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}

/// <summary>
/// Persisted event recording that a session's conversation history was compacted.
/// Contains the summary text and the reduced message set that replaces the full history.
/// </summary>
[ProtoContract]
public sealed class SessionCompacted
{
    /// <summary>Entity key of the session that was compacted.</summary>
    [ProtoMember(1)]
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>Summary text produced by the summarization reducer.</summary>
    [ProtoMember(2)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Reduced message list (typically just a system summary message) that replaces
    /// the full conversation history after compaction.
    /// </summary>
    [ProtoMember(3)]
    public List<SerializableChatMessage> CompactedMessages { get; set; } = new();

    /// <summary>Number of turns that were compacted (for metrics/diagnostics).</summary>
    [ProtoMember(4)]
    public int TurnCountBefore { get; set; }

    /// <summary>Timestamp when compaction occurred (Unix milliseconds, UTC).</summary>
    [ProtoMember(5)]
    public long CompactedAtMs { get; set; }

    /// <summary>Returns the compaction timestamp as a DateTimeOffset.</summary>
    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
