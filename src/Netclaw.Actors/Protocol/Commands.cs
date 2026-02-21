using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Command delivering user input to a session actor.
/// </summary>
[ProtoContract]
public sealed class SendUserMessage : IWithSessionId
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public string Content { get; set; } = string.Empty;
}
