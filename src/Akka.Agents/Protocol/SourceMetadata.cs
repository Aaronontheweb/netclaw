using ProtoBuf;

namespace Akka.Agents.Protocol;

/// <summary>
/// Source metadata attached to all inbound <see cref="SendUserMessage"/> commands.
/// Contains adapter type, sender identity, channel identifier, and timestamp for
/// ACL evaluation and audit logging.
/// </summary>
[ProtoContract]
public sealed class SourceMetadata
{
    /// <summary>Adapter type: "slack", "timer", "tui".</summary>
    [ProtoMember(1)]
    public string AdapterType { get; set; } = string.Empty;

    /// <summary>Sender identity (Slack user ID, "system", "local-operator", etc.).</summary>
    [ProtoMember(2)]
    public string SenderIdentity { get; set; } = string.Empty;

    /// <summary>Channel identifier (Slack channel ID, task ID, or TUI session ID).</summary>
    [ProtoMember(3)]
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Event timestamp as Unix milliseconds (UTC).</summary>
    [ProtoMember(4)]
    public long TimestampMs { get; set; }

    /// <summary>Creates source metadata from a DateTimeOffset timestamp.</summary>
    public static SourceMetadata Create(
        string adapterType,
        string senderIdentity,
        string channelId,
        DateTimeOffset timestamp) => new()
    {
        AdapterType = adapterType,
        SenderIdentity = senderIdentity,
        ChannelId = channelId,
        TimestampMs = timestamp.ToUnixTimeMilliseconds()
    };

    /// <summary>Returns the timestamp as a DateTimeOffset.</summary>
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
}
