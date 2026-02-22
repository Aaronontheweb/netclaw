using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Converts between persistence-safe <see cref="SerializableChatMessage"/> and
/// MEAI <see cref="AiChatMessage"/> types. Boundary conversion only — called
/// when preparing LLM requests and processing LLM responses.
/// </summary>
public static class ChatMessageConverter
{
    public static AiChatMessage ToAiMessage(SerializableChatMessage msg)
    {
        var role = msg.Role switch
        {
            ChatRole.User => AiChatRole.User,
            ChatRole.Assistant => AiChatRole.Assistant,
            ChatRole.System => AiChatRole.System,
            ChatRole.Tool => AiChatRole.Tool,
            _ => AiChatRole.User
        };

        return new AiChatMessage(role, msg.Content);
    }

    public static List<AiChatMessage> ToAiMessages(IEnumerable<SerializableChatMessage> messages)
    {
        return messages.Select(ToAiMessage).ToList();
    }

    public static SerializableChatMessage FromAiMessage(AiChatMessage msg)
    {
        var role = msg.Role == AiChatRole.User ? ChatRole.User
            : msg.Role == AiChatRole.Assistant ? ChatRole.Assistant
            : msg.Role == AiChatRole.System ? ChatRole.System
            : msg.Role == AiChatRole.Tool ? ChatRole.Tool
            : ChatRole.User;

        return new SerializableChatMessage
        {
            Role = role,
            Content = msg.Text ?? string.Empty
        };
    }
}
