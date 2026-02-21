using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Command delivering user input to a session actor.
/// All input adapters (Slack, timer, TUI) produce this command.
/// </summary>
[ProtoContract]
public sealed class SendUserMessage : IWithSessionId
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    /// <summary>Message content to deliver to the LLM session.</summary>
    [ProtoMember(2)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Source metadata for ACL evaluation and audit logging.</summary>
    [ProtoMember(3)]
    public SourceMetadata Source { get; set; } = new();
}
