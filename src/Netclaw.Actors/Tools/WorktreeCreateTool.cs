// -----------------------------------------------------------------------
// <copyright file="WorktreeCreateTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Configuration;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

[NetclawTool(ToolName,
    "Create a managed Git worktree for a branch. Netclaw selects the destination below the current session worktree area. " +
    "Omit SourceRepository to use the current project. A successful call changes the current project to the new worktree.",
    Grant = "file")]
public sealed partial class WorktreeCreateTool : NetclawTool<WorktreeCreateTool.Params>
{
    public const string ToolName = "worktree_create";
    private static readonly object AllocationLock = new();

    private readonly ScopedFileAccessPolicy _fileAccessPolicy;
    private readonly IGitProcessFactory _processFactory;
    private readonly TimeProvider _timeProvider;

    public record Params(
        [property: Description("Branch name for the new worktree.")] string Branch,
        [property: Description("Authorized source repository. Omit to use the current project.")] string? SourceRepository = null);

    public WorktreeCreateTool(
        ToolConfig config,
        NetclawPaths paths,
        TimeProvider timeProvider)
        : this(config, paths, timeProvider, new GitProcessFactory())
    {
    }

    internal WorktreeCreateTool(
        ToolConfig config,
        NetclawPaths paths,
        TimeProvider timeProvider,
        IGitProcessFactory processFactory)
    {
        _fileAccessPolicy = new ScopedFileAccessPolicy(config, paths);
        _timeProvider = timeProvider;
        _processFactory = processFactory;
    }

    protected override async Task<string> ExecuteAsync(
        Params args,
        ToolInvocationContext context,
        CancellationToken ct)
    {
        var branch = args.Branch?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(branch) || branch.Any(char.IsControl))
            return context.InvalidInput("Error: Branch must contain a valid non-empty Git branch name.");

        var source = string.IsNullOrWhiteSpace(args.SourceRepository)
            ? context.ProjectDirectory
            : args.SourceRepository;
        if (string.IsNullOrWhiteSpace(source))
            return context.InvalidInput("Error: SourceRepository is required when no current project is active.");

        if (!_fileAccessPolicy.TryResolveWorkingDirectory(
                source,
                context,
                out var repository,
                out var accessError,
                out var resolutionFailure))
        {
            return context.PathResolutionFailure(accessError, resolutionFailure);
        }

        if (!Directory.Exists(Path.Combine(repository, ".git"))
            && !File.Exists(Path.Combine(repository, ".git")))
        {
            return context.InvalidInput($"Error: Source repository is not a Git worktree: {repository}");
        }

        var storage = context.SessionStorage
            ?? throw new InvalidOperationException("Worktree creation requires resolved session storage.");
        var destination = AllocateDestination(storage.WorktreeDirectory, branch);

        try
        {
            using var process = _processFactory.Start(
                repository,
                ["worktree", "add", "-b", branch, destination]);
            var stdout = process.ReadStandardOutputAsync(ct);
            var stderr = process.ReadStandardErrorAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                DeleteEmptyReservation(destination);
                var detail = string.IsNullOrWhiteSpace(error)
                    ? $"Git exited with code {process.ExitCode}."
                    : error.Trim();
                return context.TransientFailure($"Error creating managed worktree: {detail}");
            }

            var canonical = Path.GetFullPath(destination);
            RecordOwnership(storage.WorktreeDirectory, canonical, context);
            var summary = string.IsNullOrWhiteSpace(output) ? string.Empty : $"\n{output.Trim()}";
            return context.SuccessProjectChange(
                $"Managed worktree created.\nPath: {canonical}\nBranch: {branch}{summary}",
                canonical,
                canonical);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                   or IOException
                                   or InvalidOperationException)
        {
            DeleteEmptyReservation(destination);
            return context.TransientFailure($"Error creating managed worktree: {ex.Message}");
        }
    }

    private static string AllocateDestination(string worktreeRoot, string branch)
    {
        var slug = new string(branch
            .Select(static character => char.IsLetterOrDigit(character) || character == '-'
                ? char.ToLowerInvariant(character)
                : '-')
            .ToArray()).Trim('-');
        if (string.IsNullOrEmpty(slug))
            slug = "worktree";

        lock (AllocationLock)
        {
            Directory.CreateDirectory(worktreeRoot);
            for (var suffix = 1; ; suffix++)
            {
                var name = suffix == 1 ? slug : $"{slug}-{suffix}";
                var candidate = Path.GetFullPath(Path.Combine(worktreeRoot, name));
                if (Directory.Exists(candidate) || File.Exists(candidate))
                    continue;

                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
    }

    private void RecordOwnership(
        string worktreeRoot,
        string destination,
        ToolInvocationContext context)
    {
        var ownershipDirectory = Path.Combine(worktreeRoot, ".ownership");
        Directory.CreateDirectory(ownershipDirectory);
        var record = new ManagedWorktreeOwnership(
            context.SessionId ?? throw new InvalidOperationException("A managed worktree requires a session."),
            destination,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var path = Path.Combine(ownershipDirectory, $"{Path.GetFileName(destination)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, WorktreeJsonContext.Default.ManagedWorktreeOwnership));
    }

    private static void DeleteEmptyReservation(string destination)
    {
        if (Directory.Exists(destination)
            && !Directory.EnumerateFileSystemEntries(destination).Any())
        {
            Directory.Delete(destination);
        }
    }
}

internal sealed record ManagedWorktreeOwnership(
    string SessionScopeId,
    string WorktreePath,
    long CreatedAtMs);

[JsonSerializable(typeof(ManagedWorktreeOwnership))]
internal sealed partial class WorktreeJsonContext : JsonSerializerContext;
