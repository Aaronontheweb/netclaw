using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Source metadata attached to inbound commands for ACL evaluation and audit logging.
/// </summary>
[ProtoContract]
public sealed class SourceMetadata
{
    [ProtoMember(1)]
    public string AdapterType { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string SenderIdentity { get; set; } = string.Empty;

    [ProtoMember(3)]
    public string ChannelId { get; set; } = string.Empty;

    [ProtoMember(4)]
    public long TimestampMs { get; set; }

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

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
}

/// <summary>
/// Well-known adapter type constants.
/// </summary>
public static class AdapterTypes
{
    public const string Slack = "slack";
    public const string Timer = "timer";
    public const string Tui = "tui";
}
