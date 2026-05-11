// -----------------------------------------------------------------------
// <copyright file="GateEvaluation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Outcome of the three-layer gate for a single tool call. Drives the
/// <c>ToolApprovalWorkflow</c> state machine on <c>LlmSessionActor</c>:
/// the workflow inspects <see cref="OverallDecision"/> to decide whether
/// to terminate immediately (HardDenied / Approved) or issue prompts
/// (NeedsPrompt with one or both of <see cref="ZonePrompt"/> /
/// <see cref="VerbPrompt"/> populated).
/// </summary>
public sealed record GateEvaluation
{
    /// <summary>The top-level decision for the call as a whole.</summary>
    public OverallGateDecision OverallDecision { get; init; }

    /// <summary>
    /// Per-clause results in source order. Always populated, even for
    /// HardDenied calls (the first hard-denied clause's reason is also
    /// surfaced on <see cref="HardDenyReason"/>).
    /// </summary>
    public IReadOnlyList<ClauseGateResult> ClauseResults { get; init; } = [];

    /// <summary>
    /// Populated when at least one clause has untrusted path operands.
    /// Carries the union of untrusted paths across all clauses for
    /// trust-all-or-nothing multi-path batching per the locked design.
    /// Null when no zone prompt is needed.
    /// </summary>
    public ZonePromptInfo? ZonePrompt { get; init; }

    /// <summary>
    /// Populated when at least one clause needs verb-pattern approval after
    /// zone gate passes. Null when no verb prompt is needed (read-only
    /// verbs in trusted zones, or verb-pattern already matched in
    /// <see cref="TrustState"/>).
    /// </summary>
    public VerbPromptInfo? VerbPrompt { get; init; }

    /// <summary>
    /// Set when <see cref="OverallDecision"/> is HardDenied. The first
    /// clause whose hard-deny pattern matched surfaces its reason here.
    /// </summary>
    public string? HardDenyReason { get; init; }

    /// <summary>
    /// Telemetry category for the hard-deny reason (e.g.
    /// <c>self_destructive</c>, <c>system_destructive</c>). Null unless
    /// <see cref="OverallDecision"/> is HardDenied.
    /// </summary>
    public string? HardDenyCategory { get; init; }

    /// <summary>
    /// True when the parser flagged the input as unparseable. Consumers
    /// route these calls to safe-fail per the spec: hard-deny still
    /// consulted; zone gate prompts on the raw command; verb gate offers
    /// only Once and Deny (no Session, no Always).
    /// </summary>
    public bool IsUnparseable { get; init; }
}

/// <summary>The three terminal states for a gate evaluation.</summary>
public enum OverallGateDecision
{
    /// <summary>
    /// All clauses passed every layer silently. Tool may execute without
    /// further user interaction.
    /// </summary>
    Approved,

    /// <summary>
    /// At least one clause needs user approval at the zone gate, the
    /// verb-pattern gate, or both. The workflow issues prompts populated
    /// from <see cref="GateEvaluation.ZonePrompt"/> and
    /// <see cref="GateEvaluation.VerbPrompt"/> in that order.
    /// </summary>
    NeedsPrompt,

    /// <summary>
    /// At least one clause matched a hard-deny rule. No prompts are
    /// offered; tool execution is refused.
    /// </summary>
    HardDenied
}

/// <summary>Per-clause result from the gate evaluator.</summary>
public sealed record ClauseGateResult
{
    public int ClauseIndex { get; init; }
    public ZoneGateDecision ZoneDecision { get; init; }
    public VerbGateDecision VerbDecision { get; init; }

    /// <summary>
    /// Untrusted paths this clause operates on. Empty when ZoneDecision
    /// is Pass or Skipped.
    /// </summary>
    public IReadOnlyList<string> UntrustedPaths { get; init; } = [];

    /// <summary>
    /// Proposed verb-pattern for the Always button when VerbDecision is
    /// Prompt. Derived as <c>&lt;verb-chain&gt; *</c> per the locked
    /// design. Null when no verb prompt is needed.
    /// </summary>
    public string? VerbPatternProposal { get; init; }

    /// <summary>The first hard-deny reason that matched this clause, or null.</summary>
    public string? HardDenyReason { get; init; }
    public string? HardDenyCategory { get; init; }
}

public enum ZoneGateDecision
{
    /// <summary>Every path in the clause is inside a trusted zone.</summary>
    Pass,

    /// <summary>One or more paths in the clause are outside trusted zones.</summary>
    Prompt,

    /// <summary>Layer 1 hard-deny fired; zone gate was not evaluated.</summary>
    Skipped
}

public enum VerbGateDecision
{
    /// <summary>
    /// Verb auto-passed because it's read-only AND all clause paths are
    /// in trusted zones, OR the clause matches an existing verb pattern.
    /// </summary>
    Pass,

    /// <summary>
    /// Mutating verb without a matching pattern (or read-only with at
    /// least one untrusted path). Workflow must prompt the user.
    /// </summary>
    Prompt,

    /// <summary>
    /// Layer 1 or Layer 2 short-circuited; verb gate was not evaluated.
    /// </summary>
    NotEvaluated
}

/// <summary>
/// Information needed to render the zone-gate prompt to the user. Holds
/// the union of untrusted paths across all clauses for the trust-all-or-
/// nothing button per the locked design.
/// </summary>
public sealed record ZonePromptInfo
{
    /// <summary>
    /// Deduplicated list of untrusted paths the user is being asked to
    /// approve. The "Trust all listed (N)" button extends
    /// <c>trustedZones</c> for every entry atomically.
    /// </summary>
    public IReadOnlyList<string> UntrustedPaths { get; init; } = [];

    /// <summary>
    /// Audience name displayed in the prompt body for context
    /// ("Allow Personal to operate inside /etc/nginx?").
    /// </summary>
    public string Audience { get; init; } = string.Empty;
}

/// <summary>
/// Information needed to render the verb-pattern gate prompt. Carries the
/// proposed pattern for the Always button plus display context.
/// </summary>
public sealed record VerbPromptInfo
{
    /// <summary>
    /// The verb-pattern glob the Always button extends, derived as
    /// <c>&lt;verb-chain&gt; *</c> per the locked design.
    /// </summary>
    public string VerbPattern { get; init; } = string.Empty;

    /// <summary>
    /// The rendered command (or first mutating clause) shown to the user
    /// for context. Not the same as the pattern — operators see what they
    /// approved AND what's being asked about.
    /// </summary>
    public string CommandText { get; init; } = string.Empty;

    /// <summary>Audience name for the prompt body.</summary>
    public string Audience { get; init; } = string.Empty;
}
