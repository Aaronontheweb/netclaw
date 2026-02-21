using Akka.Agents.Protocol;
using Akka.Agents.Routing;
using Xunit;

namespace Akka.Agents.Tests.Routing;

/// <summary>
/// Tests for SessionMessageExtractor entity key extraction logic.
/// Covers Slack, timer (schedule), and TUI entity key patterns per the
/// netclaw-input-adapters spec.
/// </summary>
public sealed class SessionMessageExtractorTests
{
    // ── Entity key extraction ────────────────────────────────────────────────

    [Fact]
    public void EntityKey_returns_key_from_SendUserMessage()
    {
        var cmd = new SendUserMessage { EntityKey = "C0123/T456" };
        Assert.Equal("C0123/T456", SessionMessageExtractor.EntityKey(cmd));
    }

    [Fact]
    public void EntityKey_returns_null_for_unrecognized_message()
    {
        Assert.Null(SessionMessageExtractor.EntityKey("not a command"));
        Assert.Null(SessionMessageExtractor.EntityKey(42));
    }

    // ── Slack key factory ────────────────────────────────────────────────────

    [Fact]
    public void SlackKey_produces_channelId_slash_threadTs_format()
    {
        var key = SessionMessageExtractor.SlackKey("C0123456", "1708531200.000100");
        Assert.Equal("C0123456/1708531200.000100", key);
    }

    [Theory]
    [InlineData("C0000001", "1700000000.000001", "C0000001/1700000000.000001")]
    [InlineData("G0123456", "1708531200.123456", "G0123456/1708531200.123456")]
    public void SlackKey_formats_correctly(string channelId, string threadTs, string expected)
    {
        Assert.Equal(expected, SessionMessageExtractor.SlackKey(channelId, threadTs));
    }

    // ── Schedule key factory ─────────────────────────────────────────────────

    [Fact]
    public void ScheduleKey_produces_schedule_slash_taskId_slash_runTs_format()
    {
        var key = SessionMessageExtractor.ScheduleKey("ebay-check", 1708531200000L);
        Assert.Equal("schedule/ebay-check/1708531200000", key);
    }

    [Theory]
    [InlineData("daily-report", 1708531200000L, "schedule/daily-report/1708531200000")]
    [InlineData("price-watch", 0L, "schedule/price-watch/0")]
    public void ScheduleKey_formats_correctly(string taskId, long runTs, string expected)
    {
        Assert.Equal(expected, SessionMessageExtractor.ScheduleKey(taskId, runTs));
    }

    // ── TUI key factory ──────────────────────────────────────────────────────

    [Fact]
    public void TuiKey_produces_tui_slash_sessionId_format()
    {
        var key = SessionMessageExtractor.TuiKey("a1b2c3");
        Assert.Equal("tui/a1b2c3", key);
    }

    // ── Key type parsing ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("C0123/1708531200.000100", EntityKeyType.Slack)]
    [InlineData("G0123456/1708531200.123456", EntityKeyType.Slack)]
    public void ParseKeyType_identifies_Slack_keys(string key, EntityKeyType expected)
    {
        Assert.Equal(expected, SessionMessageExtractor.ParseKeyType(key));
    }

    [Theory]
    [InlineData("schedule/ebay-check/1708531200000", EntityKeyType.Schedule)]
    [InlineData("schedule/daily-report/0", EntityKeyType.Schedule)]
    public void ParseKeyType_identifies_Schedule_keys(string key, EntityKeyType expected)
    {
        Assert.Equal(expected, SessionMessageExtractor.ParseKeyType(key));
    }

    [Theory]
    [InlineData("tui/a1b2c3", EntityKeyType.Tui)]
    [InlineData("tui/session-xyz", EntityKeyType.Tui)]
    public void ParseKeyType_identifies_Tui_keys(string key, EntityKeyType expected)
    {
        Assert.Equal(expected, SessionMessageExtractor.ParseKeyType(key));
    }

    // ── Routing correctness ──────────────────────────────────────────────────

    [Fact]
    public void Same_thread_produces_same_Slack_entity_key()
    {
        var channelId = "C99999";
        var threadTs = "1708531200.000100";

        var key1 = SessionMessageExtractor.SlackKey(channelId, threadTs);
        var key2 = SessionMessageExtractor.SlackKey(channelId, threadTs);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Different_runTs_produces_different_schedule_keys()
    {
        var key1 = SessionMessageExtractor.ScheduleKey("task-a", 1000L);
        var key2 = SessionMessageExtractor.ScheduleKey("task-a", 2000L);

        Assert.NotEqual(key1, key2);
    }
}
