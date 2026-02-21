using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Routing;

/// <summary>
/// Factory methods for constructing session identity keys in canonical format.
/// </summary>
public static class SessionKeys
{
    public static SessionId Slack(string channelId, string threadTs) =>
        new($"{channelId}/{threadTs}");

    public static SessionId Schedule(string taskId, long runTs) =>
        new($"schedule/{taskId}/{runTs}");

    public static SessionId Tui(string sessionId) =>
        new($"tui/{sessionId}");
}
