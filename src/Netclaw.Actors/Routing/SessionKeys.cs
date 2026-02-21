namespace Netclaw.Actors.Routing;

/// <summary>
/// Factory methods for constructing session entity keys in canonical format.
/// </summary>
public static class SessionKeys
{
    public static string Slack(string channelId, string threadTs) =>
        $"{channelId}/{threadTs}";

    public static string Schedule(string taskId, long runTs) =>
        $"schedule/{taskId}/{runTs}";

    public static string Tui(string sessionId) =>
        $"tui/{sessionId}";
}
