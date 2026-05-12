// -----------------------------------------------------------------------
// <copyright file="ToolApprovalWorkflow.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Trust-zones per-call approval workflow value type. Snapshots the
/// state machine driven by <see cref="WorkflowEngine"/> as the user
/// responds to zone-gate and verb-gate prompts. Pure record — no actor
/// or I/O concerns — so the state machine can be unit-tested
/// independently of <c>LlmSessionActor</c>.
/// </summary>
internal sealed record ToolApprovalWorkflow(
    string CallId,
    string ToolName,
    TrustAudience Audience,
    GateEvaluation Gate,
    WorkflowStage Stage);

/// <summary>
/// Workflow lifecycle stages. <c>Start → AwaitingZoneResponse →
/// AwaitingVerbResponse → Complete</c>, but any stage may
/// short-circuit to <c>Complete</c> on Deny/TimedOut, and either
/// awaiting stage is skipped when its prompt isn't needed.
/// </summary>
internal enum WorkflowStage
{
    AwaitingZoneResponse,
    AwaitingVerbResponse,
    Complete
}

/// <summary>
/// Side effect emitted by <see cref="WorkflowEngine"/>. The actor's
/// dispatcher translates each effect into the appropriate I/O call
/// (channel emit, session-grant mutation, persistent-store write,
/// approval-channel completion). Effects are emitted in the order they
/// must be applied so the actor can iterate without reordering.
/// </summary>
internal abstract record WorkflowEffect;

/// <summary>Emit the zone-gate prompt to the user.</summary>
internal sealed record EmitZonePrompt(ZonePromptInfo Prompt) : WorkflowEffect;

/// <summary>Emit the verb-gate prompt to the user.</summary>
internal sealed record EmitVerbPrompt(VerbPromptInfo Prompt) : WorkflowEffect;

/// <summary>
/// Add the path to the actor's in-memory session trusted-zone set.
/// One effect per untrusted path in a multi-path zone prompt.
/// </summary>
internal sealed record AddSessionZone(string Path) : WorkflowEffect;

/// <summary>
/// Add the verb pattern to the actor's in-memory session
/// verb-pattern set.
/// </summary>
internal sealed record AddSessionVerbPattern(string Pattern) : WorkflowEffect;

/// <summary>
/// Persist a trusted-zone grant for the audience. Written by the
/// dispatcher to <c>AudienceTrustStore.trustedZones</c>.
/// </summary>
internal sealed record PersistZoneGrant(string Path, TrustAudience Audience) : WorkflowEffect;

/// <summary>
/// Persist a verb-pattern grant for the audience. Written by the
/// dispatcher to <c>AudienceTrustStore.verbPatterns</c>.
/// </summary>
internal sealed record PersistVerbPatternGrant(string Pattern, TrustAudience Audience) : WorkflowEffect;

/// <summary>
/// Terminal effect: signals the approval channel that the call has
/// reached its final decision. Pipeline's <c>WaitForApprovalAsync</c>
/// task returns with this decision, and the pipeline either retries
/// (any Approved* variant) or renders a deny message (Denied /
/// TimedOut).
/// </summary>
internal sealed record CompleteCall(string CallId, ApprovalDecision Decision) : WorkflowEffect;
