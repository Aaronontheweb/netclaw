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

    /// <summary>
    /// Tool calls requested by the assistant. Present when role is Assistant
    /// and the LLM wants to invoke tools.
    /// </summary>
    [ProtoMember(4)]
    public List<SerializableToolCall> ToolCalls { get; set; } = new();

    /// <summary>
    /// The tool call ID this message is a result for. Present when role is Tool.
    /// </summary>
    [ProtoMember(5)]
    public string? ToolCallId { get; set; }
}

/// <summary>
/// Persistence-safe representation of a single tool call from the assistant.
/// </summary>
[ProtoContract]
public sealed class SerializableToolCall
{
    [ProtoMember(1)]
    public string CallId { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string Name { get; set; } = string.Empty;

    [ProtoMember(3)]
    public string ArgumentsJson { get; set; } = string.Empty;
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
