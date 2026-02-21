using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Command delivering user input to a session actor.
/// All input adapters (Slack, timer, TUI) produce this command.
/// </summary>
[ProtoContract]
public sealed class SendUserMessage : IWithSessionId
{
    /// <summary>
    /// Session identity key.
    /// Formats:
    ///   Slack thread:   {channelId}/{threadTs}
    ///   Scheduled task: schedule/{taskId}/{runTs}
    ///   TUI session:    tui/{sessionId}
    /// </summary>
    [ProtoMember(1)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Message content to deliver to the LLM session.</summary>
    [ProtoMember(2)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Source metadata for ACL evaluation and audit logging.</summary>
    [ProtoMember(3)]
    public SourceMetadata Source { get; set; } = new();
}
