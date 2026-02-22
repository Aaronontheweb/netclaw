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
}
