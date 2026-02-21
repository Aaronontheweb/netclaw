using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Persistence-safe representation of a chat message.
/// Never persist Microsoft.Extensions.AI types directly — use this instead.
/// </summary>
[ProtoContract]
public sealed class SerializableChatMessage
{
    [ProtoMember(1)]
    public ChatRole Role { get; set; }

    [ProtoMember(2)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Optional name (used for tool results: the tool function name).</summary>
    [ProtoMember(3)]
    public string? Name { get; set; }
}

/// <summary>
/// Role of a chat message participant. Stable integer values for wire safety.
/// </summary>
public enum ChatRole
{
    User = 0,
    Assistant = 1,
    System = 2,
    Tool = 3
}
