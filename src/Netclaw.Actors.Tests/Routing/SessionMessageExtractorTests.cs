using Netclaw.Actors.Protocol;
using Netclaw.Actors.Routing;
using Xunit;

namespace Netclaw.Actors.Tests.Routing;

public sealed class SessionMessageExtractorTests
{
    private readonly SessionMessageExtractor _extractor = new();

    [Fact]
    public void EntityId_extracts_SessionId_from_SendUserMessage()
    {
        var cmd = new SendUserMessage { SessionId = "C0123/T456" };
        Assert.Equal("C0123/T456", _extractor.EntityId(cmd));
    }

    [Fact]
    public void EntityId_returns_null_for_unrecognized_message()
    {
        Assert.Null(_extractor.EntityId("not a command"));
        Assert.Null(_extractor.EntityId(42));
    }

    [Fact]
    public void Same_SessionId_routes_to_same_shard()
    {
        var sessionId = "C99999/1708531200.000100";
        var cmd1 = new SendUserMessage { SessionId = sessionId };
        var cmd2 = new SendUserMessage { SessionId = sessionId };

        Assert.Equal(
            _extractor.ShardId(sessionId, cmd1),
            _extractor.ShardId(sessionId, cmd2));
    }

    [Fact]
    public void EntityMessage_returns_original_message()
    {
        var cmd = new SendUserMessage { SessionId = "C0123/T456", Content = "hello" };
        Assert.Same(cmd, _extractor.EntityMessage(cmd));
    }
}
