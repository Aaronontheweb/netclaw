using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Persisted event recording a completed turn (user message + assistant reply).
/// </summary>
[ProtoContract]
public sealed class TurnRecorded
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public SerializableChatMessage UserMessage { get; set; } = new();

    [ProtoMember(3)]
    public SerializableChatMessage AssistantReply { get; set; } = new();

    [ProtoMember(4)]
    public long RecordedAtMs { get; set; }

    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}

/// <summary>
/// Persisted event recording that a session's conversation history was compacted.
/// </summary>
[ProtoContract]
public sealed class SessionCompacted
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public string Summary { get; set; } = string.Empty;

    [ProtoMember(3)]
    public List<SerializableChatMessage> CompactedMessages { get; set; } = new();

    [ProtoMember(4)]
    public int TurnCountBefore { get; set; }

    [ProtoMember(5)]
    public long CompactedAtMs { get; set; }

    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
