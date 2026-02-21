using ProtoBuf;

namespace Akka.Agents.Protocol;

/// <summary>
/// Framework-owned representation of a chat message, safe for persistence and
/// serialization. Never use Microsoft.Extensions.AI types (ChatMessage, etc.)
/// directly in persisted events or snapshots — always use this type instead.
/// </summary>
[ProtoContract]
public sealed class SerializableChatMessage
{
    /// <summary>Role of the message author.</summary>
    [ProtoMember(1)]
    public ChatRole Role { get; set; }

    /// <summary>Text content of the message.</summary>
    [ProtoMember(2)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Optional name (used for tool results: the tool function name).</summary>
    [ProtoMember(3)]
    public string? Name { get; set; }
}

/// <summary>
/// Role of a chat message participant. Mapped to a stable integer for wire safety.
/// Do NOT reorder or remove values — this is part of the persistence contract.
/// </summary>
public enum ChatRole
{
    User = 0,
    Assistant = 1,
    System = 2,
    Tool = 3
}
