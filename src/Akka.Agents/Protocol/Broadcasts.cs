using ProtoBuf;

namespace Akka.Agents.Protocol;

/// <summary>
/// Published via Akka pub/sub after a session completes a turn.
/// All adapters (Slack, TUI) subscribe to receive these and deliver replies
/// through their respective channels.
/// </summary>
[ProtoContract]
public sealed class TurnBroadcast
{
    /// <summary>Entity key of the session that emitted this broadcast.</summary>
    [ProtoMember(1)]
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>The assistant reply to be delivered to the originating channel.</summary>
    [ProtoMember(2)]
    public SerializableChatMessage AssistantReply { get; set; } = new();

    /// <summary>Timestamp when this broadcast was emitted (Unix milliseconds, UTC).</summary>
    [ProtoMember(3)]
    public long BroadcastAtMs { get; set; }

    /// <summary>Returns the broadcast timestamp as a DateTimeOffset.</summary>
    public DateTimeOffset BroadcastAt => DateTimeOffset.FromUnixTimeMilliseconds(BroadcastAtMs);
}

/// <summary>
/// Published via Akka pub/sub after a session completes compaction.
/// Notifies adapters and monitoring systems that the session context was reset.
/// </summary>
[ProtoContract]
public sealed class CompactionBroadcast
{
    /// <summary>Entity key of the session that was compacted.</summary>
    [ProtoMember(1)]
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>Summary text produced by the compaction run.</summary>
    [ProtoMember(2)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>Timestamp when compaction occurred (Unix milliseconds, UTC).</summary>
    [ProtoMember(3)]
    public long CompactedAtMs { get; set; }

    /// <summary>Returns the compaction timestamp as a DateTimeOffset.</summary>
    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
