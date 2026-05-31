// -----------------------------------------------------------------------
// <copyright file="SubAgentConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for subagent timeout behavior.
/// Bound from the <c>SubAgents</c> section of <c>netclaw.json</c>.
/// All values are in seconds.
/// </summary>
public sealed class SubAgentConfig
{
    /// <summary>
    /// When false, the subagent subsystem is disabled.
    /// No subagent-based tools are registered regardless of audience profile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default timeout for subagent execution when no tool-specific override exists.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Generous wait-for-first-delta budget covering queue wait and cold prefill,
    /// mirroring <see cref="SessionConfig.PrefillTimeout"/>. Until a sub-agent's
    /// model produces its first substantive token, this budget governs the call —
    /// content-free <c>prompt_progress</c> keepalives refresh it so a healthy but
    /// slow self-hosted prefill is not mistaken for a hang. Once the first
    /// substantive delta arrives, the tighter per-agent inter-delta budget
    /// (<see cref="SubAgentProfile.TimeoutSeconds"/>) applies. A per-agent
    /// definition may override this via <c>prefillTimeoutSeconds</c> frontmatter.
    /// </summary>
    public int PrefillTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Timeout for the <c>store_memory</c> curation subagent.
    /// </summary>
    public int StoreMemoryTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Timeout for the <c>search_memories</c> retrieval subagent.
    /// </summary>
    public int SearchMemoriesTimeoutSeconds { get; set; } = 30;
}
