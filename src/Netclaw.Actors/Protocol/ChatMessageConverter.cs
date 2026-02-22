using System.Text.Json;
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

        // Tool result message: wrap content in FunctionResultContent
        if (msg.Role == ChatRole.Tool && msg.ToolCallId is not null)
        {
            var resultContent = new FunctionResultContent(msg.ToolCallId, msg.Content);
            return new AiChatMessage(role, [resultContent]);
        }

        // Assistant message with tool calls: reconstruct FunctionCallContent items
        if (msg.Role == ChatRole.Assistant && msg.ToolCalls.Count > 0)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(msg.Content))
            {
                contents.Add(new TextContent(msg.Content));
            }

            foreach (var tc in msg.ToolCalls)
            {
                IDictionary<string, object?>? args = null;
                if (!string.IsNullOrEmpty(tc.ArgumentsJson))
                {
                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.ArgumentsJson);
                }

                contents.Add(new FunctionCallContent(tc.CallId, tc.Name, args));
            }

            return new AiChatMessage(role, contents);
        }

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

        var result = new SerializableChatMessage
        {
            Role = role,
            Content = string.Empty
        };

        // Extract structured content
        foreach (var content in msg.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    // Append text (there may be text alongside tool calls)
                    result.Content = string.IsNullOrEmpty(result.Content)
                        ? text.Text ?? string.Empty
                        : result.Content + (text.Text ?? string.Empty);
                    break;

                case FunctionCallContent toolCall:
                    result.ToolCalls.Add(new SerializableToolCall
                    {
                        CallId = toolCall.CallId,
                        Name = toolCall.Name,
                        ArgumentsJson = toolCall.Arguments is not null
                            ? JsonSerializer.Serialize(toolCall.Arguments)
                            : string.Empty
                    });
                    break;

                case FunctionResultContent toolResult:
                    result.ToolCallId = toolResult.CallId;
                    result.Content = toolResult.Result?.ToString() ?? string.Empty;
                    break;
            }
        }

        // Fallback: if no structured content was found, use .Text
        if (string.IsNullOrEmpty(result.Content) && result.ToolCalls.Count == 0
            && result.ToolCallId is null)
        {
            result.Content = msg.Text ?? string.Empty;
        }

        return result;
    }
}
