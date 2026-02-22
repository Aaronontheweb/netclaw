namespace Netclaw.Actors.Sessions;

/// <summary>
/// Configuration for an LLM session actor. Carries model identity and context
/// window size so the actor can make compaction decisions.
/// </summary>
public sealed record SessionConfig
{
    /// <summary>
    /// The model identifier (e.g., "qwen3:30b", "claude-sonnet-4-20250514").
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// Maximum context window size in tokens for the configured model.
    /// Used to determine when compaction should trigger.
    /// </summary>
    public required int ContextWindowTokens { get; init; }

    /// <summary>
    /// Percentage of context window usage (0.0–1.0) at which compaction triggers.
    /// Default 0.75 — compact when 75% of the context window is consumed.
    /// </summary>
    public double CompactionThreshold { get; init; } = 0.75;

    /// <summary>
    /// Number of turns between persistence snapshots.
    /// </summary>
    public int SnapshotInterval { get; init; } = 20;

    /// <summary>
    /// Effective token limit at which compaction fires.
    /// </summary>
    public int CompactionTokenLimit => (int)(ContextWindowTokens * CompactionThreshold);
}
