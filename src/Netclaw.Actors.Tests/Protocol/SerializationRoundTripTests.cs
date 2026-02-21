using Netclaw.Actors.Protocol;
using ProtoBuf;
using Xunit;

namespace Netclaw.Actors.Tests.Protocol;

public sealed class SerializationRoundTripTests
{
    private static T RoundTrip<T>(T value)
    {
        using var ms = new MemoryStream();
        Serializer.Serialize(ms, value);
        ms.Position = 0;
        return Serializer.Deserialize<T>(ms);
    }

    [Fact]
    public void SessionId_round_trips()
    {
        var original = new SessionId("C99999/1708531200.000100");
        var result = RoundTrip(original);
        Assert.Equal(original, result);
        Assert.Equal("C99999/1708531200.000100", result.Value);
    }

    [Fact]
    public void SourceMetadata_round_trips()
    {
        var original = SourceMetadata.Create(
            adapterType: AdapterTypes.Slack,
            senderIdentity: "U12345",
            channelId: "C99999",
            timestamp: new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero));

        var result = RoundTrip(original);

        Assert.Equal(original.AdapterType, result.AdapterType);
        Assert.Equal(original.SenderIdentity, result.SenderIdentity);
        Assert.Equal(original.ChannelId, result.ChannelId);
        Assert.Equal(original.TimestampMs, result.TimestampMs);
        Assert.Equal(original.Timestamp, result.Timestamp);
    }

    [Fact]
    public void SendUserMessage_round_trips()
    {
        var original = new SendUserMessage
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            Content = "Hello, Netclaw!",
            Source = SourceMetadata.Create(AdapterTypes.Slack, "U12345", "C99999",
                new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero))
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(original.Content, result.Content);
        Assert.Equal(original.Source.AdapterType, result.Source.AdapterType);
        Assert.Equal(original.Source.SenderIdentity, result.Source.SenderIdentity);
        Assert.Equal(original.Source.ChannelId, result.Source.ChannelId);
        Assert.Equal(original.Source.TimestampMs, result.Source.TimestampMs);
    }

    [Fact]
    public void SerializableChatMessage_round_trips_user_message()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = "What is the weather on pi1?"
        };

        var result = RoundTrip(original);

        Assert.Equal(ChatRole.User, result.Role);
        Assert.Equal(original.Content, result.Content);
        Assert.Null(result.Name);
    }

    [Fact]
    public void SerializableChatMessage_round_trips_tool_message()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.Tool,
            Content = "{\"temperature\": 22}",
            Name = "get_weather"
        };

        var result = RoundTrip(original);

        Assert.Equal(ChatRole.Tool, result.Role);
        Assert.Equal(original.Content, result.Content);
        Assert.Equal("get_weather", result.Name);
    }

    [Fact]
    public void TurnRecorded_round_trips()
    {
        var ts = new DateTimeOffset(2026, 2, 21, 10, 1, 0, TimeSpan.Zero);
        var original = new TurnRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            UserMessage = new SerializableChatMessage
            {
                Role = ChatRole.User,
                Content = "Hello"
            },
            AssistantReply = new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "Hi there!"
            },
            RecordedAtMs = ts.ToUnixTimeMilliseconds()
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(ChatRole.User, result.UserMessage.Role);
        Assert.Equal("Hello", result.UserMessage.Content);
        Assert.Equal(ChatRole.Assistant, result.AssistantReply.Role);
        Assert.Equal("Hi there!", result.AssistantReply.Content);
        Assert.Equal(original.RecordedAtMs, result.RecordedAtMs);
    }

    [Fact]
    public void SessionCompacted_round_trips_with_messages()
    {
        var ts = new DateTimeOffset(2026, 2, 21, 11, 0, 0, TimeSpan.Zero);
        var original = new SessionCompacted
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            Summary = "The user asked about system status; all services healthy.",
            CompactedMessages = new List<SerializableChatMessage>
            {
                new() { Role = ChatRole.System, Content = "Summary: all services healthy." }
            },
            TurnCountBefore = 42,
            CompactedAtMs = ts.ToUnixTimeMilliseconds()
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(original.Summary, result.Summary);
        Assert.Single(result.CompactedMessages);
        Assert.Equal(ChatRole.System, result.CompactedMessages[0].Role);
        Assert.Equal("Summary: all services healthy.", result.CompactedMessages[0].Content);
        Assert.Equal(42, result.TurnCountBefore);
        Assert.Equal(original.CompactedAtMs, result.CompactedAtMs);
    }

    [Fact]
    public void TurnBroadcast_round_trips()
    {
        var ts = new DateTimeOffset(2026, 2, 21, 10, 1, 5, TimeSpan.Zero);
        var original = new TurnBroadcast
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            AssistantReply = new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "Here is your answer."
            },
            BroadcastAtMs = ts.ToUnixTimeMilliseconds()
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(ChatRole.Assistant, result.AssistantReply.Role);
        Assert.Equal("Here is your answer.", result.AssistantReply.Content);
        Assert.Equal(original.BroadcastAtMs, result.BroadcastAtMs);
    }

    [Fact]
    public void CompactionBroadcast_round_trips()
    {
        var ts = new DateTimeOffset(2026, 2, 21, 11, 0, 1, TimeSpan.Zero);
        var original = new CompactionBroadcast
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            Summary = "Context compacted after 42 turns.",
            CompactedAtMs = ts.ToUnixTimeMilliseconds()
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(original.Summary, result.Summary);
        Assert.Equal(original.CompactedAtMs, result.CompactedAtMs);
    }
}
