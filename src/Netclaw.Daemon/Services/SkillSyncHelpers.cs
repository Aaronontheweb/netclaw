// -----------------------------------------------------------------------
// <copyright file="SkillSyncHelpers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Daemon.Services;

internal static class SkillSyncHelpers
{
    internal static readonly string[] AllowedResourcePrefixes = ["references", "scripts", "assets"];

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    internal static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    internal static string? ValidateResourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
            return null;

        var normalized = path.Replace('\\', '/');
        var firstSegment = normalized.Split('/')[0];
        if (!AllowedResourcePrefixes.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            return null;

        return normalized;
    }

    internal static SkillSyncState ReadSyncState(string path, ILogger logger)
    {
        if (!File.Exists(path))
            return new SkillSyncState();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SkillSyncState>(json) ?? new SkillSyncState();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read sync state at {Path} — starting fresh", path);
            return new SkillSyncState();
        }
    }

    internal static void WriteSyncState(string path, SkillSyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(state, IndentedJsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Removes skills that are no longer present in a server feed's index from
    /// both the on-disk feed directory and the sync state.
    /// </summary>
    /// <remarks>
    /// Only the server-feed sync path calls this. The system-skill feed
    /// deliberately keeps removed skills on disk — its CDN manifest is the sole
    /// source of built-in skills, so a transient empty manifest must never wipe
    /// them. A private server feed is, by contrast, authoritative for its own
    /// <c>.server-feeds/{name}/</c> namespace (which nothing else writes to), so
    /// dropping skills the server no longer advertises is the correct,
    /// non-destructive behavior.
    /// <para>
    /// Callers MUST gate this on a successful, non-empty index fetch. Pruning
    /// against an empty or failed response would delete every locally synced
    /// skill on a transient server outage.
    /// </para>
    /// </remarks>
    /// <param name="feedDir">The feed's on-disk root (e.g. <c>.server-feeds/{name}</c>).</param>
    /// <param name="serverSkillNames">Skill names present in the freshly fetched, non-empty server index.</param>
    /// <param name="syncState">Sync state to prune in place.</param>
    /// <param name="logger">Logger for prune diagnostics.</param>
    /// <returns><c>true</c> if any sync-state entry or on-disk directory was removed.</returns>
    internal static bool PruneRemovedSkills(
        string feedDir,
        IReadOnlyCollection<string> serverSkillNames,
        SkillSyncState syncState,
        ILogger logger)
    {
        var present = new HashSet<string>(serverSkillNames, StringComparer.Ordinal);
        var changed = false;

        // 1) Prune stale sync-state entries.
        var staleStateKeys = syncState.Skills.Keys
            .Where(name => !present.Contains(name))
            .ToList();
        foreach (var name in staleStateKeys)
        {
            syncState.Skills.Remove(name);
            changed = true;
        }

        // 2) Prune stale on-disk directories. The feed root also holds this
        //    service's own bookkeeping (.staging from ReplaceSkillDirectoryAsync,
        //    .sync-state.json) — skip anything dot-prefixed so we only ever touch
        //    skill directories that the sync service itself created.
        if (Directory.Exists(feedDir))
        {
            foreach (var dir in Directory.GetDirectories(feedDir))
            {
                var dirName = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(dirName) || dirName.StartsWith('.'))
                    continue;

                if (present.Contains(dirName))
                    continue;

                try
                {
                    Directory.Delete(dir, recursive: true);
                    logger.LogInformation(
                        "Pruned removed skill '{SkillName}' from feed directory {FeedDir}",
                        dirName, feedDir);
                    changed = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Failed to prune removed skill '{SkillName}' from {FeedDir} — leaving in place",
                        dirName, feedDir);
                }
            }
        }

        return changed;
    }

    internal static async Task ReplaceSkillDirectoryAsync(
        string parentDirectory,
        string skillName,
        IReadOnlyList<DownloadedSkillFile> files,
        CancellationToken cancellationToken)
    {
        var skillDir = Path.Combine(parentDirectory, skillName);
        var stagingRoot = Path.Combine(parentDirectory, ".staging");
        Directory.CreateDirectory(stagingRoot);

        var stagingDir = Path.Combine(stagingRoot, $"{skillName}-{Guid.NewGuid():N}");
        var backupDir = Path.Combine(stagingRoot, $"{skillName}-backup-{Guid.NewGuid():N}");

        Directory.CreateDirectory(stagingDir);

        try
        {
            foreach (var file in files)
            {
                var targetPath = Path.Combine(stagingDir, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await File.WriteAllTextAsync(targetPath, file.Content, cancellationToken);
            }

            if (Directory.Exists(skillDir))
                Directory.Move(skillDir, backupDir);

            Directory.Move(stagingDir, skillDir);

            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(skillDir) && Directory.Exists(backupDir))
                Directory.Move(backupDir, skillDir);

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);

            if (Directory.Exists(backupDir) && !Directory.Exists(skillDir))
                Directory.Delete(backupDir, recursive: true);
        }
    }
}

internal sealed record DownloadedSkillFile(string RelativePath, string Content);
