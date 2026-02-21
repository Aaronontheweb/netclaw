using Akka.Agents.Protocol;

namespace Akka.Agents.Routing;

/// <summary>
/// Extracts entity keys from session commands and provides factory methods for
/// constructing entity keys in the correct format for each input source.
///
/// Entity key formats:
///   Slack thread:   {channelId}/{threadTs}
///   Scheduled task: schedule/{taskId}/{runTs}
///   TUI session:    tui/{sessionId}
///
/// Note: The actual IMessageExtractor (Akka.Cluster.Sharding) integration is
/// wired in Task 1.3 (Session parent and entity routing). This class provides
/// the key extraction logic independently of the Akka sharding contract.
/// </summary>
public static class SessionMessageExtractor
{
    /// <summary>
    /// Extracts the entity key from an inbound message.
    /// Returns <c>null</c> for messages that are not routable to a session actor.
    /// </summary>
    public static string? EntityKey(object message) => message switch
    {
        SendUserMessage cmd => cmd.EntityKey,
        _ => null
    };

    /// <summary>
    /// Constructs an entity key for a Slack thread session.
    /// </summary>
    /// <param name="channelId">Slack channel ID (e.g., "C0123456").</param>
    /// <param name="threadTs">Thread timestamp (e.g., "1708531200.123456").</param>
    public static string SlackKey(string channelId, string threadTs) =>
        $"{channelId}/{threadTs}";

    /// <summary>
    /// Constructs an entity key for a scheduled task execution.
    /// Each timer fire creates a fresh entity key to ensure isolation.
    /// </summary>
    /// <param name="taskId">The stable task identifier (e.g., "ebay-check").</param>
    /// <param name="runTs">Unix milliseconds of the timer fire timestamp.</param>
    public static string ScheduleKey(string taskId, long runTs) =>
        $"schedule/{taskId}/{runTs}";

    /// <summary>
    /// Constructs an entity key for a TUI chat session.
    /// </summary>
    /// <param name="sessionId">Unique session identifier for this TUI session.</param>
    public static string TuiKey(string sessionId) =>
        $"tui/{sessionId}";

    /// <summary>
    /// Parses an entity key and returns its type.
    /// </summary>
    public static EntityKeyType ParseKeyType(string entityKey)
    {
        if (entityKey.StartsWith("schedule/", StringComparison.Ordinal))
            return EntityKeyType.Schedule;
        if (entityKey.StartsWith("tui/", StringComparison.Ordinal))
            return EntityKeyType.Tui;
        return EntityKeyType.Slack;
    }
}

/// <summary>
/// The category of session entity key, indicating which input adapter produced the message.
/// </summary>
public enum EntityKeyType
{
    /// <summary>Originated from a Slack thread ({channelId}/{threadTs}).</summary>
    Slack = 0,

    /// <summary>Originated from a scheduled task timer (schedule/{taskId}/{runTs}).</summary>
    Schedule = 1,

    /// <summary>Originated from the TUI chat adapter (tui/{sessionId}).</summary>
    Tui = 2
}
