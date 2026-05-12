// -----------------------------------------------------------------------
// <copyright file="WorkflowEngineTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Trust-zones tasks 6.1-6.3: pure state-machine tests for the per-call
/// approval workflow. The workflow drives the sequential two-prompt
/// UX (zone gate → verb gate → complete) without touching any actor
/// or I/O concern; this suite exercises the engine in isolation by
/// asserting <c>(state, effects)</c> pairs for every transition.
///
/// Wire-up of the actor-side dispatcher lives in §6.4; this file
/// covers the logic that decides what effects to emit, not how they're
/// dispatched.
/// </summary>
public sealed class WorkflowEngineTests
{
    private const string CallId = "call-abc";
    private const string ToolName = "shell_execute";
    private const TrustAudience Audience = TrustAudience.Personal;

    // -----------------------------------------------------------------
    // Start: auto-allow and hard-deny short-circuits
    // -----------------------------------------------------------------

    [Fact]
    public void Start_with_Approved_gate_completes_immediately_with_ApprovedOnce()
    {
        // Defensive completeness: the executor handles Approved gates
        // before throwing ToolApprovalRequiredException, so the
        // workflow shouldn't normally see this. If it does (e.g.
        // future refactor routes auto-allow through the workflow),
        // we still terminate correctly with no prompts.
        var gate = NewGate(OverallGateDecision.Approved);

        var (state, effects) = WorkflowEngine.Start(CallId, ToolName, Audience, gate);

        Assert.Equal(WorkflowStage.Complete, state.Stage);
        AssertSingleCompleteCall(effects, ApprovalDecision.ApprovedOnce);
        Assert.Empty(effects.OfType<EmitZonePrompt>());
        Assert.Empty(effects.OfType<EmitVerbPrompt>());
    }

    [Fact]
    public void Start_with_HardDenied_gate_terminates_with_Denied_no_prompt()
    {
        // Hard-deny is a non-negotiable termination — no prompt, no
        // grant. The CompleteCall(Denied) lets the pipeline render a
        // refusal message; the user has no override.
        var gate = NewGate(OverallGateDecision.HardDenied);

        var (state, effects) = WorkflowEngine.Start(CallId, ToolName, Audience, gate);

        Assert.Equal(WorkflowStage.Complete, state.Stage);
        AssertSingleCompleteCall(effects, ApprovalDecision.Denied);
        Assert.Empty(effects.OfType<EmitZonePrompt>());
        Assert.Empty(effects.OfType<EmitVerbPrompt>());
    }

    [Fact]
    public void Start_with_NeedsPrompt_but_neither_prompt_populated_completes_safely()
    {
        // Invariant violation from the gate evaluator — defensive
        // path so the workflow doesn't hang waiting for a prompt
        // that will never arrive.
        var gate = NewGate(OverallGateDecision.NeedsPrompt, zone: null, verb: null);

        var (state, effects) = WorkflowEngine.Start(CallId, ToolName, Audience, gate);

        Assert.Equal(WorkflowStage.Complete, state.Stage);
        AssertSingleCompleteCall(effects, ApprovalDecision.ApprovedOnce);
    }

    // -----------------------------------------------------------------
    // Start: zone-only / verb-only / both-prompts initial dispatch
    // -----------------------------------------------------------------

    [Fact]
    public void Start_with_zone_prompt_only_emits_zone_prompt_and_awaits_zone()
    {
        // Read-only verb operating on an untrusted path: zone gate
        // prompts; verb gate auto-passes once the zone clears.
        var zone = NewZonePrompt("/etc/nginx");
        var gate = NewGate(OverallGateDecision.NeedsPrompt, zone: zone, verb: null);

        var (state, effects) = WorkflowEngine.Start(CallId, ToolName, Audience, gate);

        Assert.Equal(WorkflowStage.AwaitingZoneResponse, state.Stage);
        var emit = Assert.Single(effects.OfType<EmitZonePrompt>());
        Assert.Same(zone, emit.Prompt);
        Assert.Empty(effects.OfType<EmitVerbPrompt>());
        Assert.Empty(effects.OfType<CompleteCall>());
    }

    [Fact]
    public void Start_with_verb_prompt_only_emits_verb_prompt_and_awaits_verb()
    {
        // Mutating verb in an already-trusted zone: zone gate
        // auto-passes; verb gate prompts.
        var verb = NewVerbPrompt("rm *");
        var gate = NewGate(OverallGateDecision.NeedsPrompt, zone: null, verb: verb);

        var (state, effects) = WorkflowEngine.Start(CallId, ToolName, Audience, gate);

        Assert.Equal(WorkflowStage.AwaitingVerbResponse, state.Stage);
        var emit = Assert.Single(effects.OfType<EmitVerbPrompt>());
        Assert.Same(verb, emit.Prompt);
        Assert.Empty(effects.OfType<EmitZonePrompt>());
    }

    [Fact]
    public void Start_with_both_prompts_emits_zone_first_and_awaits_zone()
    {
        // Mutating verb on an untrusted path: zone gate fires first,
        // verb gate stays queued until zone clears.
        var zone = NewZonePrompt("/etc/nginx");
        var verb = NewVerbPrompt("rm *");
        var gate = NewGate(OverallGateDecision.NeedsPrompt, zone, verb);

        var (state, effects) = WorkflowEngine.Start(CallId, ToolName, Audience, gate);

        Assert.Equal(WorkflowStage.AwaitingZoneResponse, state.Stage);
        Assert.Single(effects.OfType<EmitZonePrompt>());
        Assert.Empty(effects.OfType<EmitVerbPrompt>());
    }

    // -----------------------------------------------------------------
    // Zone-stage response handling
    // -----------------------------------------------------------------

    [Fact]
    public void Zone_response_ApprovedOnce_with_no_verb_prompt_completes()
    {
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, NewZonePrompt("/etc"), verb: null));

        var (final, effects) = WorkflowEngine.OnResponse(start, ApprovalDecision.ApprovedOnce);

        Assert.Equal(WorkflowStage.Complete, final.Stage);
        AssertSingleCompleteCall(effects, ApprovalDecision.ApprovedOnce);
        Assert.Empty(effects.OfType<AddSessionZone>());
        Assert.Empty(effects.OfType<PersistZoneGrant>());
    }

    [Fact]
    public void Zone_response_ApprovedSession_writes_session_zones_and_advances_to_verb()
    {
        var zone = NewZonePrompt("/etc/nginx", "/var/log");
        var verb = NewVerbPrompt("sed *");
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, zone, verb));

        var (next, effects) = WorkflowEngine.OnResponse(start, ApprovalDecision.ApprovedSession);

        Assert.Equal(WorkflowStage.AwaitingVerbResponse, next.Stage);
        var sessionZones = effects.OfType<AddSessionZone>().Select(s => s.Path).ToList();
        Assert.Equal(["/etc/nginx", "/var/log"], sessionZones);
        Assert.Single(effects.OfType<EmitVerbPrompt>());
        Assert.Empty(effects.OfType<CompleteCall>());  // not yet complete
        Assert.Empty(effects.OfType<PersistZoneGrant>());
    }

    [Fact]
    public void Zone_response_ApprovedAlways_writes_persistent_zones_and_advances_to_verb()
    {
        var zone = NewZonePrompt("/etc/nginx");
        var verb = NewVerbPrompt("sed *");
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, zone, verb));

        var (next, effects) = WorkflowEngine.OnResponse(start, ApprovalDecision.ApprovedAlways);

        Assert.Equal(WorkflowStage.AwaitingVerbResponse, next.Stage);
        var persisted = Assert.Single(effects.OfType<PersistZoneGrant>());
        Assert.Equal("/etc/nginx", persisted.Path);
        Assert.Equal(Audience, persisted.Audience);
        Assert.Empty(effects.OfType<AddSessionZone>());
    }

    [Fact]
    public void Zone_response_ApprovedEverywhere_writes_persistent_zones()
    {
        // Trust-zones "Everywhere" at the zone gate has the same on-disk
        // effect as Always (zones don't have a global-wildcard form —
        // they're always concrete directories). The persisted scope
        // distinction collapses into one effect; the user's chosen
        // ApprovalDecision is preserved on the persistence call so
        // telemetry can still differentiate the click later.
        var zone = NewZonePrompt("/srv");
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, zone, verb: null));

        var (next, effects) = WorkflowEngine.OnResponse(start, ApprovalDecision.ApprovedEverywhere);

        Assert.Equal(WorkflowStage.Complete, next.Stage);
        Assert.Single(effects.OfType<PersistZoneGrant>());
        AssertSingleCompleteCall(effects, ApprovalDecision.ApprovedOnce);
    }

    [Fact]
    public void Zone_response_Denied_terminates_with_no_grant_written()
    {
        var zone = NewZonePrompt("/etc");
        var verb = NewVerbPrompt("rm *");
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, zone, verb));

        var (final, effects) = WorkflowEngine.OnResponse(start, ApprovalDecision.Denied);

        Assert.Equal(WorkflowStage.Complete, final.Stage);
        AssertSingleCompleteCall(effects, ApprovalDecision.Denied);
        Assert.Empty(effects.OfType<AddSessionZone>());
        Assert.Empty(effects.OfType<PersistZoneGrant>());
        Assert.Empty(effects.OfType<EmitVerbPrompt>());  // verb prompt never fires
    }

    [Fact]
    public void Zone_response_TimedOut_terminates_with_no_grant_written()
    {
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, NewZonePrompt("/etc"), NewVerbPrompt("rm *")));

        var (final, effects) = WorkflowEngine.OnResponse(start, ApprovalDecision.TimedOut);

        Assert.Equal(WorkflowStage.Complete, final.Stage);
        AssertSingleCompleteCall(effects, ApprovalDecision.TimedOut);
        Assert.Empty(effects.OfType<AddSessionZone>());
    }

    // -----------------------------------------------------------------
    // Verb-stage response handling
    // -----------------------------------------------------------------

    [Fact]
    public void Verb_response_ApprovedSession_writes_session_pattern_and_completes()
    {
        var verb = NewVerbPrompt("git push origin main *");
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, zone: null, verb: verb));

        var (final, effects) = WorkflowEngine.OnResponse(start, ApprovalDecision.ApprovedSession);

        Assert.Equal(WorkflowStage.Complete, final.Stage);
        var added = Assert.Single(effects.OfType<AddSessionVerbPattern>());
        Assert.Equal("git push origin main *", added.Pattern);
        AssertSingleCompleteCall(effects, ApprovalDecision.ApprovedOnce);
    }

    [Fact]
    public void Verb_response_ApprovedAlways_writes_persistent_pattern_and_completes()
    {
        var verb = NewVerbPrompt("dotnet test *");
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, zone: null, verb: verb));

        var (final, effects) = WorkflowEngine.OnResponse(start, ApprovalDecision.ApprovedAlways);

        Assert.Equal(WorkflowStage.Complete, final.Stage);
        var persisted = Assert.Single(effects.OfType<PersistVerbPatternGrant>());
        Assert.Equal("dotnet test *", persisted.Pattern);
        Assert.Equal(Audience, persisted.Audience);
        Assert.Empty(effects.OfType<AddSessionVerbPattern>());
    }

    [Fact]
    public void Verb_response_ApprovedOnce_writes_no_grant_but_completes_approved()
    {
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, zone: null, verb: NewVerbPrompt("rm *")));

        var (final, effects) = WorkflowEngine.OnResponse(start, ApprovalDecision.ApprovedOnce);

        Assert.Equal(WorkflowStage.Complete, final.Stage);
        Assert.Empty(effects.OfType<AddSessionVerbPattern>());
        Assert.Empty(effects.OfType<PersistVerbPatternGrant>());
        AssertSingleCompleteCall(effects, ApprovalDecision.ApprovedOnce);
    }

    [Fact]
    public void Verb_response_Denied_at_second_stage_keeps_zone_grant_from_first_stage()
    {
        // The user explicitly approved the geography at the zone gate —
        // that grant lives even if they decline the specific mutating
        // verb. Only the current call is denied.
        var zone = NewZonePrompt("/repo");
        var verb = NewVerbPrompt("rm *");
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.NeedsPrompt, zone, verb));

        var (afterZone, zoneEffects) = WorkflowEngine.OnResponse(start, ApprovalDecision.ApprovedSession);
        Assert.Single(zoneEffects.OfType<AddSessionZone>());  // zone grant landed

        var (final, verbEffects) = WorkflowEngine.OnResponse(afterZone, ApprovalDecision.Denied);

        Assert.Equal(WorkflowStage.Complete, final.Stage);
        Assert.Empty(verbEffects.OfType<AddSessionZone>());  // no retroactive write
        Assert.Empty(verbEffects.OfType<AddSessionVerbPattern>());
        AssertSingleCompleteCall(verbEffects, ApprovalDecision.Denied);
    }

    // -----------------------------------------------------------------
    // Misuse / invariant guards
    // -----------------------------------------------------------------

    [Fact]
    public void OnResponse_throws_when_workflow_already_completed()
    {
        var (start, _) = WorkflowEngine.Start(
            CallId, ToolName, Audience,
            NewGate(OverallGateDecision.HardDenied));
        Assert.Equal(WorkflowStage.Complete, start.Stage);

        Assert.Throws<InvalidOperationException>(
            () => WorkflowEngine.OnResponse(start, ApprovalDecision.ApprovedOnce));
    }

    [Fact]
    public void Start_throws_on_null_or_empty_call_id()
    {
        // ArgumentException.ThrowIfNullOrEmpty raises ArgumentNullException
        // for null and ArgumentException for empty — both are subtypes of
        // ArgumentException, so ThrowsAny covers the contract.
        var gate = NewGate(OverallGateDecision.Approved);
        Assert.ThrowsAny<ArgumentException>(() => WorkflowEngine.Start("", ToolName, Audience, gate));
        Assert.ThrowsAny<ArgumentException>(() => WorkflowEngine.Start(null!, ToolName, Audience, gate));
    }

    [Fact]
    public void Start_throws_on_null_gate()
    {
        Assert.Throws<ArgumentNullException>(
            () => WorkflowEngine.Start(CallId, ToolName, Audience, null!));
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static GateEvaluation NewGate(
        OverallGateDecision decision,
        ZonePromptInfo? zone = null,
        VerbPromptInfo? verb = null) => new()
        {
            OverallDecision = decision,
            ZonePrompt = zone,
            VerbPrompt = verb
        };

    private static ZonePromptInfo NewZonePrompt(params string[] paths) => new()
    {
        UntrustedPaths = paths,
        Audience = nameof(TrustAudience.Personal)
    };

    private static VerbPromptInfo NewVerbPrompt(string pattern) => new()
    {
        VerbPattern = pattern,
        CommandText = pattern,
        Audience = nameof(TrustAudience.Personal)
    };

    private static void AssertSingleCompleteCall(
        IReadOnlyList<WorkflowEffect> effects,
        ApprovalDecision expected)
    {
        var complete = Assert.Single(effects.OfType<CompleteCall>());
        Assert.Equal(CallId, complete.CallId);
        Assert.Equal(expected, complete.Decision);
    }
}
