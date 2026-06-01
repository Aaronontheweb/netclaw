// -----------------------------------------------------------------------
// <copyright file="LifecycleEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Netclaw.Daemon.Services;

namespace Netclaw.Daemon.Lifecycle;

/// <summary>Request to shut down the daemon, sourced from query string.</summary>
public sealed record ShutdownDaemonRequest([FromQuery(Name = "reason")] string? Reason);

/// <summary>Successful shutdown acknowledgement: echoes the reason and reports the daemon PID.</summary>
public sealed record ShutdownDaemonResponse(string Reason, int Pid);

/// <summary>Successful restart acknowledgement: echoes the reason and reports the daemon PID.</summary>
public sealed record RestartDaemonResponse(string Reason, int Pid);

/// <summary>Error payload returned when a lifecycle request is malformed.</summary>
public sealed record LifecycleErrorResponse(string Error);

public static class LifecycleEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        // Daemon lifecycle endpoint — CLI calls this before sending SIGTERM.
        // Config-triggered restart coordination happens inside DaemonRestartCoordinator.
        app.MapPost("/api/lifecycle/shutdown", Results<Ok<ShutdownDaemonResponse>, BadRequest<LifecycleErrorResponse>> (
            [AsParameters] ShutdownDaemonRequest request,
            DaemonLifecycleNotifier notifier) =>
        {
            if (string.IsNullOrEmpty(request.Reason))
                return TypedResults.BadRequest(new LifecycleErrorResponse("reason query parameter is required"));

            notifier.NotifyShutdown(request.Reason);
            return TypedResults.Ok(new ShutdownDaemonResponse(request.Reason, Environment.ProcessId));
        })
        .WithName("ShutdownDaemon")
        .WithSummary("Request a graceful daemon shutdown ahead of SIGTERM.")
        .WithTags("Lifecycle")
        .RequireAuthorization();

        // In-process restart. Unlike shutdown, this rebuilds the host (re-reading
        // ALL config, including the Daemon/bind section the config watcher refuses
        // to hot-reload) while the same process keeps the singleton lock — so it
        // stays the supervisor's child and never spawns a detached replacement.
        // The CLI uses this to apply `netclaw init` config under a container
        // supervisor instead of stopping + spawning a daemon (#1279). The reason
        // is fixed to "config-reload" by DaemonRestartCoordinator, so there is no
        // request body to parameterize.
        app.MapPost("/api/lifecycle/restart", async Task<Ok<RestartDaemonResponse>> (
            IDaemonRestartCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            await coordinator.RequestConfigRestartAsync(cancellationToken);
            return TypedResults.Ok(new RestartDaemonResponse("config-reload", Environment.ProcessId));
        })
        .WithName("RestartDaemon")
        .WithSummary("Request a graceful in-process daemon restart to apply config changes.")
        .WithTags("Lifecycle")
        .RequireAuthorization();

        return app;
    }
}
