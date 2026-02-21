using Netclaw.Actors.Routing;
using Xunit;

namespace Netclaw.Actors.Tests.Routing;

public sealed class SessionKeysTests
{
    [Fact]
    public void Slack_produces_channelId_slash_threadTs()
    {
        Assert.Equal("C0123456/1708531200.000100", SessionKeys.Slack("C0123456", "1708531200.000100"));
    }

    [Theory]
    [InlineData("C0000001", "1700000000.000001", "C0000001/1700000000.000001")]
    [InlineData("G0123456", "1708531200.123456", "G0123456/1708531200.123456")]
    public void Slack_formats_correctly(string channelId, string threadTs, string expected)
    {
        Assert.Equal(expected, SessionKeys.Slack(channelId, threadTs));
    }

    [Fact]
    public void Schedule_produces_schedule_slash_taskId_slash_runTs()
    {
        Assert.Equal("schedule/ebay-check/1708531200000", SessionKeys.Schedule("ebay-check", 1708531200000L));
    }

    [Theory]
    [InlineData("daily-report", 1708531200000L, "schedule/daily-report/1708531200000")]
    [InlineData("price-watch", 0L, "schedule/price-watch/0")]
    public void Schedule_formats_correctly(string taskId, long runTs, string expected)
    {
        Assert.Equal(expected, SessionKeys.Schedule(taskId, runTs));
    }

    [Fact]
    public void Tui_produces_tui_slash_sessionId()
    {
        Assert.Equal("tui/a1b2c3", SessionKeys.Tui("a1b2c3"));
    }

    [Fact]
    public void Same_inputs_produce_same_Slack_key()
    {
        var key1 = SessionKeys.Slack("C99999", "1708531200.000100");
        var key2 = SessionKeys.Slack("C99999", "1708531200.000100");
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Different_runTs_produces_different_Schedule_keys()
    {
        var key1 = SessionKeys.Schedule("task-a", 1000L);
        var key2 = SessionKeys.Schedule("task-a", 2000L);
        Assert.NotEqual(key1, key2);
    }
}
