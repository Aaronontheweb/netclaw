using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using ChatRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// Tests boundary conversion between persistence-safe <see cref="SerializableChatMessage"/>
/// and MEAI <see cref="AiChatMessage"/> types.
/// </summary>
public class ChatMessageConverterTests
{
    public static TheoryData<ChatRole, AiChatRole> RoleMappings => new()
    {
        { ChatRole.User, AiChatRole.User },
        { ChatRole.Assistant, AiChatRole.Assistant },
        { ChatRole.System, AiChatRole.System },
        { ChatRole.Tool, AiChatRole.Tool },
    };

    [Theory]
    [MemberData(nameof(RoleMappings))]
    public void ToAiMessage_maps_role_correctly(ChatRole inputRole, AiChatRole expectedAiRole)
    {
        var msg = new SerializableChatMessage { Role = inputRole, Content = "test" };
        var ai = ChatMessageConverter.ToAiMessage(msg);
        Assert.Equal(expectedAiRole, ai.Role);
        Assert.Equal("test", ai.Text);
    }

    [Theory]
    [MemberData(nameof(RoleMappings))]
    public void FromAiMessage_maps_role_correctly(ChatRole expectedRole, AiChatRole aiRole)
    {
        var ai = new AiChatMessage(aiRole, "test");
        var msg = ChatMessageConverter.FromAiMessage(ai);
        Assert.Equal(expectedRole, msg.Role);
        Assert.Equal("test", msg.Content);
    }

    [Fact]
    public void ToAiMessage_preserves_content()
    {
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = "Hello, how are you?"
        };

        var ai = ChatMessageConverter.ToAiMessage(msg);

        Assert.Equal("Hello, how are you?", ai.Text);
    }

    [Fact]
    public void FromAiMessage_handles_null_text()
    {
        // ChatMessage with no text content
        var ai = new AiChatMessage(AiChatRole.Assistant, (string?)null);

        var msg = ChatMessageConverter.FromAiMessage(ai);

        Assert.Equal(ChatRole.Assistant, msg.Role);
        Assert.Equal(string.Empty, msg.Content);
    }

    [Fact]
    public void ToAiMessages_converts_full_conversation()
    {
        var messages = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.System, Content = "You are helpful." },
            new() { Role = ChatRole.User, Content = "Hello" },
            new() { Role = ChatRole.Assistant, Content = "Hi there!" },
            new() { Role = ChatRole.User, Content = "What time is it?" },
        };

        var aiMessages = ChatMessageConverter.ToAiMessages(messages);

        Assert.Equal(4, aiMessages.Count);
        Assert.Equal(AiChatRole.System, aiMessages[0].Role);
        Assert.Equal(AiChatRole.User, aiMessages[1].Role);
        Assert.Equal(AiChatRole.Assistant, aiMessages[2].Role);
        Assert.Equal(AiChatRole.User, aiMessages[3].Role);

        Assert.Equal("You are helpful.", aiMessages[0].Text);
        Assert.Equal("Hello", aiMessages[1].Text);
        Assert.Equal("Hi there!", aiMessages[2].Text);
        Assert.Equal("What time is it?", aiMessages[3].Text);
    }

    [Fact]
    public void Round_trip_preserves_role_and_content()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "Here is my response."
        };

        var ai = ChatMessageConverter.ToAiMessage(original);
        var roundTripped = ChatMessageConverter.FromAiMessage(ai);

        Assert.Equal(original.Role, roundTripped.Role);
        Assert.Equal(original.Content, roundTripped.Content);
    }

    [Fact]
    public void ToAiMessages_empty_list_returns_empty()
    {
        var result = ChatMessageConverter.ToAiMessages(Array.Empty<SerializableChatMessage>());
        Assert.Empty(result);
    }

    // ── Tool call / result round-trip tests ──

    [Fact]
    public void FromAiMessage_captures_tool_calls_from_assistant()
    {
        var contents = new List<AIContent>
        {
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" }),
            new FunctionCallContent("call-2", "fetch",
                new Dictionary<string, object?> { ["url"] = "https://example.com" })
        };
        var ai = new AiChatMessage(AiChatRole.Assistant, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai);

        Assert.Equal(ChatRole.Assistant, msg.Role);
        Assert.Equal(2, msg.ToolCalls.Count);
        Assert.Equal("call-1", msg.ToolCalls[0].CallId);
        Assert.Equal("web_search", msg.ToolCalls[0].Name);
        Assert.Equal("call-2", msg.ToolCalls[1].CallId);
        Assert.Equal("fetch", msg.ToolCalls[1].Name);
    }

    [Fact]
    public void ToAiMessage_reconstructs_tool_calls()
    {
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            {
                new SerializableToolCall
                {
                    CallId = "call-1",
                    Name = "web_search",
                    ArgumentsJson = """{"query":"test"}"""
                }
            }
        };

        var ai = ChatMessageConverter.ToAiMessage(msg);

        Assert.Equal(AiChatRole.Assistant, ai.Role);
        var toolCall = Assert.Single(ai.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-1", toolCall.CallId);
        Assert.Equal("web_search", toolCall.Name);
    }

    [Fact]
    public void Tool_result_message_round_trips()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.Tool,
            Content = "Found 3 results",
            ToolCallId = "call-1",
            Name = "web_search"
        };

        var ai = ChatMessageConverter.ToAiMessage(original);
        Assert.Equal(AiChatRole.Tool, ai.Role);

        var resultContent = Assert.Single(ai.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-1", resultContent.CallId);
        Assert.Equal("Found 3 results", resultContent.Result?.ToString());

        var roundTripped = ChatMessageConverter.FromAiMessage(ai);
        Assert.Equal(ChatRole.Tool, roundTripped.Role);
        Assert.Equal("call-1", roundTripped.ToolCallId);
        Assert.Equal("Found 3 results", roundTripped.Content);
    }

    [Fact]
    public void Assistant_message_with_text_and_tool_calls_preserves_both()
    {
        var contents = new List<AIContent>
        {
            new TextContent("Let me search for that."),
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        };
        var ai = new AiChatMessage(AiChatRole.Assistant, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai);

        Assert.Equal("Let me search for that.", msg.Content);
        Assert.Single(msg.ToolCalls);
        Assert.Equal("web_search", msg.ToolCalls[0].Name);
    }
}
