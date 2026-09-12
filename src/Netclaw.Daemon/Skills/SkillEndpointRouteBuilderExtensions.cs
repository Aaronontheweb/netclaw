// -----------------------------------------------------------------------
// <copyright file="SkillEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;

namespace Netclaw.Daemon.Skills;

/// <summary>
/// Read endpoint over the daemon's live <see cref="SkillRegistry"/>. This is the
/// authoritative source of what an agent can actually load — file skills PLUS the
/// dynamic MCP prompt skills a filesystem scan can never see. The CLI's
/// <c>skill list</c> is served by this endpoint and requires the daemon; there is
/// no disk fallback.
/// </summary>
public static class SkillEndpointRouteBuilderExtensions
{
    public static void MapSkillEndpoints(this WebApplication app)
    {
        app.MapGet("/api/skills", (SkillRegistry registry, NetclawPaths paths) =>
                (Ok<SkillInventory.Response>)TypedResults.Ok(
                    SkillInventory.From(registry.GetAll(), paths)))
            .WithName("ListSkills")
            .WithSummary("List every skill the daemon has loaded, including dynamic MCP prompt skills.")
            .WithTags("Skills")
            .RequireAuthorization();

        app.MapPost("/api/skills/sync", async Task<IResult> (
                bool? retryRejected,
                IRequiredActor<ServerFeedSkillSyncActorKey> syncActor,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var response = await syncActor.ActorRef.Ask<SkillSyncResult.Response>(
                        retryRejected == true
                            ? ServerFeedSkillSyncActor.Run.RetryRejectedCommits
                            : ServerFeedSkillSyncActor.Run.Instance,
                        cancellationToken);
                    return TypedResults.Ok(response);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Skill sync stopped",
                        detail: "The daemon stopped the active skill sync pass.");
                }
            })
            .WithName("SyncSkills")
            .WithSummary("Run the configured external skill sync pass.")
            .WithTags("Skills")
            .RequireAuthorization();

        app.MapGet("/api/skills/plugins", async Task<IResult> (
                GitSkillPluginManagementService service,
                CancellationToken cancellationToken) =>
            TypedResults.Ok(await service.ListAsync(cancellationToken)))
            .WithName("ListGitSkillPlugins")
            .WithSummary("List configured Git skill plugins and their installed state.")
            .WithTags("Skills")
            .RequireAuthorization();

        app.MapPost("/api/skills/plugins", async Task<IResult> (
                GitSkillPluginApi.InstallRequest request,
                GitSkillPluginManagementService service,
                DaemonRestartSignal restartSignal,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var source = await service.InstallAsync(request, cancellationToken);
                    return TypedResults.Ok(new GitSkillPluginApi.InstallResponse
                    {
                        RestartGeneration = restartSignal.Generation,
                        Plugin = GitSkillPluginManagementService.ToRow(
                            source,
                            GitSkillPluginApi.PluginStatus.NotInstalled,
                            null),
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return PluginProblem(ex);
                }
            })
            .WithName("InstallGitSkillPlugin")
            .WithSummary("Validate and configure one public GitHub skill plugin.")
            .WithTags("Skills")
            .RequireAuthorization();

        app.MapPatch("/api/skills/plugins/{name}", IResult (
                string name,
                GitSkillPluginApi.SetEnabledRequest request,
                GitSkillPluginManagementService service,
                DaemonRestartSignal restartSignal) =>
            {
                try
                {
                    var changed = service.SetEnabled(name, request.Enabled);
                    return TypedResults.Ok(new GitSkillPluginApi.MutationResponse
                    {
                        RestartGeneration = restartSignal.Generation,
                        Name = name,
                        Changed = changed,
                    });
                }
                catch (Exception ex)
                {
                    return PluginProblem(ex);
                }
            })
            .WithName("SetGitSkillPluginEnabled")
            .WithSummary("Enable or disable one Git skill plugin.")
            .WithTags("Skills")
            .RequireAuthorization();

        app.MapDelete("/api/skills/plugins/{name}", IResult (
                string name,
                GitSkillPluginManagementService service,
                DaemonRestartSignal restartSignal) =>
            {
                try
                {
                    var changed = service.Remove(name);
                    return TypedResults.Ok(new GitSkillPluginApi.MutationResponse
                    {
                        RestartGeneration = restartSignal.Generation,
                        Name = name,
                        Changed = changed,
                    });
                }
                catch (Exception ex)
                {
                    return PluginProblem(ex);
                }
            })
            .WithName("RemoveGitSkillPlugin")
            .WithSummary("Remove one configured Git skill plugin.")
            .WithTags("Skills")
            .RequireAuthorization();
    }

    private static IResult PluginProblem(Exception exception)
    {
        var (status, title) = exception switch
        {
            GitSkillPluginConfigException { Failure: GitSkillPluginConfigFailure.NotFound }
                => (StatusCodes.Status404NotFound, "Plugin not found"),
            GitSkillPluginConfigException { Failure: GitSkillPluginConfigFailure.Conflict }
                => (StatusCodes.Status409Conflict, "Plugin conflict"),
            GitSkillPluginConfigException
                => (StatusCodes.Status400BadRequest, "Plugin configuration rejected"),
            GitSkillPluginRejectedException
                => (StatusCodes.Status422UnprocessableEntity, "Plugin candidate rejected"),
            GitSkillPluginScannerUnavailableException
                => (StatusCodes.Status503ServiceUnavailable, "Plugin scanner unavailable"),
            TimeoutException
                => (StatusCodes.Status504GatewayTimeout, "Plugin request timed out"),
            HttpRequestException
                => (StatusCodes.Status502BadGateway, "GitHub request failed"),
            InvalidDataException
                => (StatusCodes.Status502BadGateway, "GitHub response rejected"),
            InvalidOperationException
                => (StatusCodes.Status400BadRequest, "Plugin request rejected"),
            _ => (StatusCodes.Status500InternalServerError, "Plugin operation failed"),
        };
        var detail = status == StatusCodes.Status500InternalServerError
            ? "The plugin operation failed. Check the daemon logs."
            : exception.Message;
        return TypedResults.Problem(statusCode: status, title: title, detail: detail);
    }
}
