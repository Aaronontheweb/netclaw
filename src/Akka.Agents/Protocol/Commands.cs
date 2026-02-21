using ProtoBuf;

namespace Akka.Agents.Protocol;

/// <summary>
/// Universal command contract for delivering user input to a session actor.
/// All input adapters (Slack, timer, TUI) produce this command — session actors
/// never reference adapter-specific types.
/// </summary>
[ProtoContract]
public sealed class SendUserMessage
{
    /// <summary>
    /// Entity key identifying the target session actor.
    /// Formats:
    ///   Slack thread:   {channelId}/{threadTs}
    ///   Scheduled task: schedule/{taskId}/{runTs}
    ///   TUI session:    tui/{sessionId}
    /// </summary>
    [ProtoMember(1)]
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>Message content to deliver to the LLM session.</summary>
    [ProtoMember(2)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Source metadata for ACL evaluation and audit logging.</summary>
    [ProtoMember(3)]
    public SourceMetadata Source { get; set; } = new();
}
