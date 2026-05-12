// -----------------------------------------------------------------------
// <copyright file="SessionDependencies.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Memory;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Core runtime services required by every session actor.
/// </summary>
public sealed record SessionServices(
    IChatClientProvider ClientProvider,
    ISystemPromptProvider PromptProvider,
    IReadOnlyList<IContextLayerProvider> ContextLayers,
    TimeProvider TimeProvider,
    NetclawPaths Paths);

/// <summary>
/// Tool execution infrastructure. Null when the session operates without tools.
/// </summary>
public sealed record SessionToolServices(
    IToolExecutor ToolExecutor,
    IToolAuditLogger? AuditLogger,
    ToolRegistry ToolRegistry,
    ToolAccessPolicy? AccessPolicy,
    TrustContextDeriver? TrustDeriver,
    Skills.SkillRegistry? SkillRegistry,
    // Trust-zones persistent store for `Always`/`Everywhere` clicks at
    // either gate. Required: a session actor that supports tool flows
    // must be able to persist Always/Everywhere grants if the user
    // clicks them — silently dropping the persistence on a missing
    // store is a security-relevant fail-soft we refuse to do. Wire
    // alongside `GateEvaluator` and `TrustStateComposer` in DI.
    Netclaw.Configuration.AudienceTrustStore AudienceTrustStore,
    IToolApprovalService? ApprovalService = null,
    SubAgentDefinitionRegistry? SubAgentRegistry = null,
    SubAgentSpawner? SubAgentSpawner = null);

/// <summary>
/// Memory infrastructure for recall, checkpoint, and curation.
/// </summary>
public sealed record SessionMemoryServices(
    IMemoryExtractor MemoryExtractor,
    IMemoryRecallCoordinator RecallCoordinator,
    IMemoryCheckpointSink CheckpointSink,
    SQLiteMemoryStore? MemoryStore,
    MemoryConfig? MemoryConfig = null);

/// <summary>
/// Metrics and lifecycle observation.
/// </summary>
public sealed record SessionObservability(
    Telemetry.ISessionMetrics? Metrics,
    ISessionLifecycleObserver? LifecycleObserver);
