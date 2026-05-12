// -----------------------------------------------------------------------
// <copyright file="WorkflowEngine.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Pure functional core of the trust-zones per-call approval workflow.
/// Given a <see cref="GateEvaluation"/> and a sequence of user
/// <see cref="ApprovalDecision"/>s, produces the side-effect list the
/// actor's dispatcher applies to channels, grant stores, and the
/// approval channel.
/// </summary>
/// <remarks>
/// All methods are static and deterministic so the state machine can
/// be exercised in unit tests without spinning up an actor system.
/// The actor side becomes a thin shell that pipes events in and
/// dispatches effects out — the imperative shell over this functional
/// core.
///
/// Sequencing per the trust-zones design:
/// <list type="number">
/// <item>Hard-denied gates terminate at Start with <see cref="CompleteCall"/>(<see cref="ApprovalDecision.Denied"/>).</item>
/// <item>Approved gates terminate at Start with <see cref="CompleteCall"/>(<see cref="ApprovalDecision.ApprovedOnce"/>).</item>
/// <item>NeedsPrompt with a zone prompt issues the zone prompt first.</item>
/// <item>After zone Approved, the verb prompt (if any) is issued.</item>
/// <item>Deny / TimedOut at either stage short-circuits to Complete; zone
/// grants approved earlier in the same workflow are kept (the user
/// approved geography even if they declined the verb).</item>
/// </list>
/// </remarks>
internal static class WorkflowEngine
{
    /// <summary>
    /// Initializes a workflow for a tool call and returns the initial
    /// state plus the effects to apply immediately. The actor calls
    /// this when the executor throws <c>ToolApprovalRequiredException</c>
    /// carrying the gate evaluation.
    /// </summary>
    public static (ToolApprovalWorkflow State, IReadOnlyList<WorkflowEffect> Effects) Start(
        string callId,
        string toolName,
        TrustAudience audience,
        GateEvaluation gate)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentNullException.ThrowIfNull(gate);

        // Approved and HardDenied SHOULD be handled by the executor /
        // policy before the workflow is reached — the gate evaluator's
        // terminal decisions don't need user interaction. Defensive
        // handling here keeps the workflow correct if a caller routes
        // them through anyway.
        if (gate.OverallDecision == OverallGateDecision.Approved)
        {
            return (
                new ToolApprovalWorkflow(callId, toolName, audience, gate, WorkflowStage.Complete),
                [new CompleteCall(callId, ApprovalDecision.ApprovedOnce)]);
        }

        if (gate.OverallDecision == OverallGateDecision.HardDenied)
        {
            return (
                new ToolApprovalWorkflow(callId, toolName, audience, gate, WorkflowStage.Complete),
                [new CompleteCall(callId, ApprovalDecision.Denied)]);
        }

        // NeedsPrompt: zone first, then verb. A NeedsPrompt evaluation
        // with neither prompt populated is an invariant violation from
        // the gate evaluator. Defensive: complete as approved rather
        // than hanging the call waiting for a prompt that will never
        // come.
        if (gate.ZonePrompt is not null)
        {
            return (
                new ToolApprovalWorkflow(callId, toolName, audience, gate, WorkflowStage.AwaitingZoneResponse),
                [new EmitZonePrompt(gate.ZonePrompt)]);
        }

        if (gate.VerbPrompt is not null)
        {
            return (
                new ToolApprovalWorkflow(callId, toolName, audience, gate, WorkflowStage.AwaitingVerbResponse),
                [new EmitVerbPrompt(gate.VerbPrompt)]);
        }

        return (
            new ToolApprovalWorkflow(callId, toolName, audience, gate, WorkflowStage.Complete),
            [new CompleteCall(callId, ApprovalDecision.ApprovedOnce)]);
    }

    /// <summary>
    /// Advances the workflow with a user decision. Returns the next
    /// state and the effects to apply in order. Throws if the workflow
    /// is already complete — the actor's dispatcher should drop the
    /// response in that case rather than re-applying it.
    /// </summary>
    public static (ToolApprovalWorkflow State, IReadOnlyList<WorkflowEffect> Effects) OnResponse(
        ToolApprovalWorkflow current,
        ApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(current);

        return current.Stage switch
        {
            WorkflowStage.AwaitingZoneResponse => OnZoneResponse(current, decision),
            WorkflowStage.AwaitingVerbResponse => OnVerbResponse(current, decision),
            WorkflowStage.Complete => throw new InvalidOperationException(
                $"Workflow for call {current.CallId} is already complete."),
            _ => throw new InvalidOperationException(
                $"Unknown workflow stage {current.Stage}."),
        };
    }

    private static (ToolApprovalWorkflow, IReadOnlyList<WorkflowEffect>) OnZoneResponse(
        ToolApprovalWorkflow current,
        ApprovalDecision decision)
    {
        var effects = new List<WorkflowEffect>();

        if (decision is ApprovalDecision.Denied or ApprovalDecision.TimedOut)
        {
            // No zone grant written; user declined the geography. The
            // verb prompt is not offered — denying the zone implies
            // denying any verb that needs untrusted paths.
            effects.Add(new CompleteCall(current.CallId, decision));
            return (current with { Stage = WorkflowStage.Complete }, effects);
        }

        // ApprovedOnce / ApprovedSession / ApprovedAlways /
        // ApprovedEverywhere all advance past the zone gate. Persistence
        // varies by scope:
        //   Once       — no grant; the retry uses the workflow-approved
        //                bypass set by the dispatcher.
        //   Session    — AddSessionZone per untrusted path; lives until
        //                the actor restarts.
        //   Always /
        //   Everywhere — PersistZoneGrant per untrusted path; written to
        //                AudienceTrustStore.trustedZones.
        //
        // The zone prompt is trust-all-or-nothing, so every untrusted
        // path in ZonePrompt.UntrustedPaths flows through together.
        var untrustedPaths = current.Gate.ZonePrompt?.UntrustedPaths ?? [];
        if (decision == ApprovalDecision.ApprovedSession)
        {
            foreach (var path in untrustedPaths)
                effects.Add(new AddSessionZone(path));
        }
        else if (decision is ApprovalDecision.ApprovedAlways or ApprovalDecision.ApprovedEverywhere)
        {
            foreach (var path in untrustedPaths)
                effects.Add(new PersistZoneGrant(path, current.Audience));
        }

        // Advance: emit the verb prompt if needed; otherwise complete.
        // When the verb gate already auto-passed (read-only verb in
        // what-will-now-be a trusted zone), no second prompt is
        // required. CompleteCall always uses ApprovedOnce as the
        // terminal signal — the pipeline doesn't need to know the
        // user's scope choice because the workflow already wrote the
        // grants via effects, and the retry-bypass is set by the
        // dispatcher on any Approved* outcome regardless of scope.
        if (current.Gate.VerbPrompt is not null)
        {
            effects.Add(new EmitVerbPrompt(current.Gate.VerbPrompt));
            return (current with { Stage = WorkflowStage.AwaitingVerbResponse }, effects);
        }

        effects.Add(new CompleteCall(current.CallId, ApprovalDecision.ApprovedOnce));
        return (current with { Stage = WorkflowStage.Complete }, effects);
    }

    private static (ToolApprovalWorkflow, IReadOnlyList<WorkflowEffect>) OnVerbResponse(
        ToolApprovalWorkflow current,
        ApprovalDecision decision)
    {
        var effects = new List<WorkflowEffect>();

        if (decision is ApprovalDecision.Denied or ApprovalDecision.TimedOut)
        {
            // Verb deny terminates the call but does NOT roll back any
            // zone grant that was approved at the prior stage. The user
            // explicitly accepted the geography; only the mutating verb
            // was rejected.
            effects.Add(new CompleteCall(current.CallId, decision));
            return (current with { Stage = WorkflowStage.Complete }, effects);
        }

        var pattern = current.Gate.VerbPrompt?.VerbPattern;
        if (!string.IsNullOrEmpty(pattern))
        {
            if (decision == ApprovalDecision.ApprovedSession)
            {
                effects.Add(new AddSessionVerbPattern(pattern));
            }
            else if (decision is ApprovalDecision.ApprovedAlways or ApprovalDecision.ApprovedEverywhere)
            {
                effects.Add(new PersistVerbPatternGrant(pattern, current.Audience));
            }
        }

        effects.Add(new CompleteCall(current.CallId, ApprovalDecision.ApprovedOnce));
        return (current with { Stage = WorkflowStage.Complete }, effects);
    }
}
