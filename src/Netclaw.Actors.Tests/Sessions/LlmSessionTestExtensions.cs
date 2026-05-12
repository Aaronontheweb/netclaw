// -----------------------------------------------------------------------
// <copyright file="LlmSessionTestExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Sessions;

internal static class LlmSessionTestExtensions
{
    public static IServiceCollection AddLlmSessionCompositeRecords(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<NetclawPaths>()));

        services.TryAddSingleton(sp => new SessionMemoryServices(
            sp.GetService<IMemoryExtractor>() ?? NullMemoryExtractor.Instance,
            sp.GetService<IMemoryRecallCoordinator>() ?? NullMemoryRecallCoordinator.Instance,
            sp.GetService<IMemoryCheckpointSink>() ?? NullMemoryCheckpointSink.Instance,
            sp.GetService<SQLiteMemoryStore>()));

        services.TryAddSingleton(sp => new SessionObservability(
            sp.GetService<Telemetry.ISessionMetrics>(),
            sp.GetService<ISessionLifecycleObserver>()));

        if (services.Any(d => d.ServiceType == typeof(IToolExecutor)))
        {
            // AudienceTrustStore is required on SessionToolServices so the
            // workflow dispatcher can persist Always/Everywhere grants.
            // Tests that don't exercise trust-zones still need one wired
            // through — register a unique per-test temp-file-backed store
            // unless the test set one up itself.
            services.TryAddSingleton(_ => new Netclaw.Configuration.AudienceTrustStore(
                Path.Combine(Path.GetTempPath(),
                    $"netclaw-test-trust-zones-{Guid.NewGuid():N}.json")));

            services.TryAddSingleton(sp => new SessionToolServices(
                sp.GetRequiredService<IToolExecutor>(),
                sp.GetService<IToolAuditLogger>(),
                sp.GetRequiredService<ToolRegistry>(),
                sp.GetService<ToolAccessPolicy>(),
                sp.GetService<TrustContextDeriver>(),
                sp.GetService<SkillRegistry>(),
                sp.GetRequiredService<Netclaw.Configuration.AudienceTrustStore>(),
                sp.GetService<IToolApprovalService>(),
                sp.GetService<SubAgentDefinitionRegistry>(),
                sp.GetService<SubAgentSpawner>()));
        }

        return services;
    }
}
