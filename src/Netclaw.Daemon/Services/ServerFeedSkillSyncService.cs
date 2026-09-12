// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Security.Skills;
using Netclaw.SkillClient;

namespace Netclaw.Daemon.Services;

internal interface IServerFeedSkillSyncRunner
{
    Task<SkillSyncResult.Response> SyncAsync(
        bool retryRejected,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs one skill sync pass through all configured skill-server instances.
/// Each source remains independent, so one source failure does not block another.
/// </summary>
internal sealed class ServerFeedSkillSyncService : IServerFeedSkillSyncRunner
{
    private const string ArchiveType = "archive";
    private const string SkillFileName = "SKILL.md";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly SkillFeedsConfig _feedsConfig;
    private readonly NetclawPaths _paths;
    private readonly SkillInventoryRefresher _inventoryRefresher;
    private readonly TimeProvider _timeProvider;
    private readonly ISkillContentScanner _scanner;
    private readonly ILogger<ServerFeedSkillSyncService> _logger;
    private readonly Func<SkillFeedSource, SkillServerClient> _clientFactory;
    private readonly GitSkillPluginStateStore _pluginStateStore;
    private readonly IGitSkillPluginAcquirer _pluginAcquirer;
    private readonly IOperationalNotificationSink _notificationSink;
    private bool _startupPluginCleanupComplete;

    public ServerFeedSkillSyncService(
        SkillFeedsConfig feedsConfig,
        NetclawPaths paths,
        SkillInventoryRefresher inventoryRefresher,
        TimeProvider timeProvider,
        ISkillContentScanner scanner,
        ILogger<ServerFeedSkillSyncService> logger,
        GitSkillPluginStateStore pluginStateStore,
        IGitSkillPluginAcquirer pluginAcquirer,
        IOperationalNotificationSink notificationSink)
        : this(
            feedsConfig,
            paths,
            inventoryRefresher,
            timeProvider,
            scanner,
            logger,
            CreateSkillServerClient,
            pluginStateStore,
            pluginAcquirer,
            notificationSink)
    {
    }

    internal ServerFeedSkillSyncService(
        SkillFeedsConfig feedsConfig,
        NetclawPaths paths,
        SkillRegistry skillRegistry,
        SkillIndexPublisher skillIndexPublisher,
        TimeProvider timeProvider,
        ISkillContentScanner scanner,
        ILogger<ServerFeedSkillSyncService> logger,
        IReadOnlyList<ResolvedExternalSource> externalSources,
        Func<SkillFeedSource, SkillServerClient> clientFactory,
        GitSkillPluginStateStore pluginStateStore,
        IGitSkillPluginAcquirer pluginAcquirer,
        IOperationalNotificationSink notificationSink)
        : this(
            feedsConfig,
            paths,
            new SkillInventoryRefresher(
                paths,
                feedsConfig,
                externalSources,
                skillRegistry,
                skillIndexPublisher),
            timeProvider,
            scanner,
            logger,
            clientFactory,
            pluginStateStore,
            pluginAcquirer,
            notificationSink)
    {
    }

    private ServerFeedSkillSyncService(
        SkillFeedsConfig feedsConfig,
        NetclawPaths paths,
        SkillInventoryRefresher inventoryRefresher,
        TimeProvider timeProvider,
        ISkillContentScanner scanner,
        ILogger<ServerFeedSkillSyncService> logger,
        Func<SkillFeedSource, SkillServerClient> clientFactory,
        GitSkillPluginStateStore pluginStateStore,
        IGitSkillPluginAcquirer pluginAcquirer,
        IOperationalNotificationSink notificationSink)
    {
        _feedsConfig = feedsConfig;
        _paths = paths;
        _inventoryRefresher = inventoryRefresher;
        _timeProvider = timeProvider;
        _scanner = scanner;
        _logger = logger;
        _clientFactory = clientFactory;
        _pluginStateStore = pluginStateStore;
        _pluginAcquirer = pluginAcquirer;
        _notificationSink = notificationSink;
    }

    /// <summary>
    /// Runs one complete synchronization pass.
    /// </summary>
    public Task<SkillSyncResult.Response> SyncAsync(CancellationToken cancellationToken)
        => SyncAsync(retryRejected: false, cancellationToken);

    public async Task<SkillSyncResult.Response> SyncAsync(
        bool retryRejected,
        CancellationToken cancellationToken)
    {
        var passId = Guid.NewGuid().ToString("N");
        _logger.LogInformation("External skill sync pass started. {PassId}", passId);
        var outcome = "failed";
        try
        {
            var sources = new List<SkillSyncResult.SourceRow>();
            foreach (var feed in _feedsConfig.Feeds.Where(static feed => feed.Enabled))
            {
                try
                {
                    sources.Add(await SyncFeedAsync(feed, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Server feed sync failed for '{FeedName}' ({FeedUrl}) — using on-disk skills",
                        feed.Name, feed.Url);
                    sources.Add(new SkillSyncResult.SourceRow
                    {
                        Name = feed.Name,
                        FailedCount = 1,
                        Sidecar = "not-run",
                        Error = "The source sync failed. Existing files remain in use.",
                    });
                }
            }

            var managedPluginSources = await SyncManagedGitPluginsAsync(
                sources,
                retryRejected,
                cancellationToken);

            try
            {
                var scan = RescanAndUpdateIndex(managedPluginSources);
                var result = new SkillSyncResult.Response
                {
                    PassId = passId,
                    Sources = sources,
                    Inventory = new SkillSyncResult.InventoryRow
                    {
                        Succeeded = true,
                        AcceptedCount = scan.AcceptedSkills.Count,
                        RejectedCount = scan.Issues.Count,
                    },
                };
                outcome = "completed";
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Skill inventory publication after server feed sync failed");
                var result = new SkillSyncResult.Response
                {
                    PassId = passId,
                    Sources = sources,
                    Inventory = new SkillSyncResult.InventoryRow
                    {
                        Succeeded = false,
                        Error = "The skill inventory refresh failed.",
                    },
                };
                outcome = "completed";
                return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            _logger.LogInformation("External skill sync pass {Outcome}. {PassId}", outcome, passId);
        }
    }

    private async Task<IReadOnlyList<ResolvedExternalSource>> SyncManagedGitPluginsAsync(
        List<SkillSyncResult.SourceRow> rows,
        bool retryRejected,
        CancellationToken cancellationToken)
    {
        var configuredSources = _feedsConfig.Plugins;
        if (!GitSkillPluginSourceValidator.TryValidateSources(configuredSources, out var validationError))
        {
            _logger.LogWarning("Managed Git plugin configuration is invalid: {Error}", validationError);
            return ResolveManagedGitPluginSources(
                await _pluginStateStore.LoadReceiptsAsync(cancellationToken));
        }

        await _pluginStateStore.RemoveSourcesExceptAsync(
            configuredSources.Select(static source => source.Name).ToArray(), cancellationToken);
        var receipts = await _pluginStateStore.LoadReceiptsAsync(cancellationToken);

        if (!_startupPluginCleanupComplete)
        {
            CleanupManagedGitPluginDirectories(receipts);
            _startupPluginCleanupComplete = true;
        }

        foreach (var source in configuredSources.Where(static source => source.Enabled))
        {
            try
            {
                rows.Add(await SyncManagedGitPluginAsync(
                    source,
                    retryRejected,
                    cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Managed Git plugin sync failed for '{PluginName}' — keeping the prior publication",
                    source.Name);
                rows.Add(PluginFailure(source.Name));
            }
        }

        receipts = await _pluginStateStore.LoadReceiptsAsync(cancellationToken);
        return ResolveManagedGitPluginSources(receipts);
    }

    private async Task<SkillSyncResult.SourceRow> SyncManagedGitPluginAsync(
        GitSkillPluginSource source,
        bool retryRejected,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(source.TimeoutSeconds), _timeProvider);
        using var sourceOperation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        var sourceToken = sourceOperation.Token;
        var sourceFingerprint = GitSkillPluginSourceValidator.Fingerprint(source);
        var receipt = await _pluginStateStore.GetReceiptAsync(source.Name, sourceToken);
        var commit = source.ReferenceKind == GitSkillPluginReferenceKind.Commit
            ? source.Reference.ToLowerInvariant()
            : await _pluginAcquirer.ResolveCommitAsync(source, sourceToken);
        var installedDirectory = receipt is null
            ? null
            : _paths.ManagedGitSkillCommitDirectory(
                receipt.SourceName, receipt.SourceFingerprint, receipt.InstalledCommit);

        if (receipt is not null
            && string.Equals(receipt.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)
            && Directory.Exists(installedDirectory)
            && (string.Equals(receipt.InstalledCommit, commit, StringComparison.Ordinal)
                || string.Equals(receipt.LastObservedCommit, commit, StringComparison.Ordinal)))
        {
            return PluginUnchanged(source.Name, receipt.InstalledCommit, receipt.InstalledVersion);
        }

        var rejection = await _pluginStateStore.GetRejectionAsync(
            source.Name, sourceFingerprint, commit, sourceToken);
        if (rejection is not null && !retryRejected)
        {
            if (rejection.SecurityRejection
                && !rejection.AlertEmitted
                && await _pluginStateStore.TryClaimSecurityAlertAsync(
                    source.Name, sourceFingerprint, commit, sourceToken))
            {
                EmitSecurityRejectionAlert(sourceFingerprint, commit);
            }

            _logger.LogInformation(
                "Managed Git plugin '{PluginName}' commit {Commit} remains rejected",
                source.Name, commit);
            return PluginRejected(source.Name, commit);
        }

        try
        {
            var candidate = await _pluginAcquirer.AcquireAsync(source, commit, sourceToken);
            if (!Directory.Exists(candidate.Directory))
                throw new IOException("The immutable managed Git plugin candidate is missing.");

            if (receipt is not null
                && string.Equals(receipt.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)
                && Directory.Exists(installedDirectory)
                && candidate.Version is not null
                && string.Equals(candidate.Version, receipt.InstalledVersion, StringComparison.Ordinal))
            {
                if (rejection is null)
                {
                    await _pluginStateStore.UpdateLastObservedCommitAsync(
                        source.Name, candidate.Commit, sourceToken);
                }
                else
                {
                    await _pluginStateStore.UpdateLastObservedCommitAfterRetryAsync(
                        source.Name, sourceFingerprint, candidate.Commit, sourceToken);
                }
                DeleteUnpublishedCandidate(candidate.Directory, installedDirectory);
                return PluginUnchanged(source.Name, receipt.InstalledCommit, receipt.InstalledVersion);
            }

            if (rejection is null)
            {
                await _pluginStateStore.SaveReceiptAsync(
                    source, candidate.Commit, candidate.Version, sourceToken);
            }
            else
            {
                await _pluginStateStore.SaveReceiptAfterRetryAsync(
                    source, candidate.Commit, candidate.Version, sourceToken);
            }
            return new SkillSyncResult.SourceRow
            {
                Name = source.Name,
                ChangedCount = 1,
                Sidecar = "not-applicable",
                Commit = candidate.Commit,
                Version = candidate.Version,
            };
        }
        catch (GitSkillPluginRejectedException rejectionException)
        {
            await _pluginStateStore.SaveRejectionAsync(
                source.Name,
                sourceFingerprint,
                rejectionException.Commit,
                rejectionException.Message,
                rejectionException.SecurityRejection,
                sourceToken);

            if (rejectionException.SecurityRejection
                && await _pluginStateStore.TryClaimSecurityAlertAsync(
                    source.Name, sourceFingerprint, rejectionException.Commit, sourceToken))
            {
                EmitSecurityRejectionAlert(sourceFingerprint, rejectionException.Commit);
            }

            _logger.LogWarning(
                "Managed Git plugin '{PluginName}' commit {Commit} was rejected",
                source.Name, rejectionException.Commit);
            return PluginRejected(source.Name, rejectionException.Commit);
        }
    }

    private IReadOnlyList<ResolvedExternalSource> ResolveManagedGitPluginSources(
        IReadOnlyList<GitSkillPluginReceipt> receipts)
        => receipts
            .Where(receipt => _feedsConfig.Plugins.Any(source => source.Enabled
                && string.Equals(source.Name, receipt.SourceName, StringComparison.Ordinal)))
            .Select(receipt => new
            {
                Receipt = receipt,
                Directory = _paths.ManagedGitSkillCommitDirectory(
                    receipt.SourceName, receipt.SourceFingerprint, receipt.InstalledCommit),
            })
            .Where(static candidate => Directory.Exists(candidate.Directory))
            .OrderBy(static candidate => candidate.Receipt.SourceName, StringComparer.Ordinal)
            .Select(static candidate => new ResolvedExternalSource(
                $"managed-git:{candidate.Receipt.SourceName}",
                [candidate.Directory],
                AllowSymlinks: false))
            .ToArray();

    private void EmitSecurityRejectionAlert(string sourceFingerprint, string commit)
    {
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "skill.plugin.security_rejected",
            AlertType.SkillPluginSecurityRejected,
            "A managed Git skill plugin failed a security check.",
            AlertSeverity.Warning,
            $"{sourceFingerprint}:{commit}",
            new Dictionary<string, string>
            {
                ["source_fingerprint"] = sourceFingerprint,
                ["commit"] = commit,
            }));
    }

    private void CleanupManagedGitPluginDirectories(IReadOnlyList<GitSkillPluginReceipt> receipts)
    {
        var selectedDirectories = receipts
            .Select(receipt => Path.GetFullPath(_paths.ManagedGitSkillCommitDirectory(
                receipt.SourceName, receipt.SourceFingerprint, receipt.InstalledCommit)))
            .ToHashSet(StringComparer.Ordinal);
        var root = _paths.ManagedGitSkillsDirectory;
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var sourceDirectory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    CleanupManagedGitPluginSourceDirectory(sourceDirectory, selectedDirectories);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Managed Git plugin cleanup failed for source directory {Directory}",
                        sourceDirectory);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Managed Git plugin cleanup could not enumerate {Directory}", root);
        }
    }

    private void CleanupManagedGitPluginSourceDirectory(
        string sourceDirectory,
        HashSet<string> selectedDirectories)
    {
        DeleteManagedGitPluginDirectory(Path.Combine(sourceDirectory, ".staging"));

        foreach (var fingerprintDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            if (string.Equals(
                    Path.GetFileName(fingerprintDirectory),
                    ".staging",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var commitsDirectory = Path.Combine(fingerprintDirectory, "commits");
            if (!Directory.Exists(commitsDirectory))
                continue;

            foreach (var commitDirectory in Directory.EnumerateDirectories(commitsDirectory))
            {
                if (!selectedDirectories.Contains(Path.GetFullPath(commitDirectory)))
                    DeleteManagedGitPluginDirectory(commitDirectory);
            }
        }
    }

    private void DeleteManagedGitPluginDirectory(string directory)
    {
        try
        {
            GitSkillPluginAcquirer.DeleteDirectory(directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Managed Git plugin cleanup failed for directory {Directory}", directory);
        }
    }

    private void DeleteUnpublishedCandidate(string candidateDirectory, string installedDirectory)
    {
        if (!string.Equals(Path.GetFullPath(candidateDirectory), Path.GetFullPath(installedDirectory), StringComparison.Ordinal)
            && Directory.Exists(candidateDirectory))
        {
            GitSkillPluginAcquirer.DeleteDirectory(candidateDirectory);
        }
    }

    private static SkillSyncResult.SourceRow PluginUnchanged(string name, string commit, string? version) => new()
    {
        Name = name,
        UnchangedCount = 1,
        Sidecar = "not-applicable",
        Commit = commit,
        Version = version,
    };

    private static SkillSyncResult.SourceRow PluginRejected(string name, string commit) => new()
    {
        Name = name,
        RejectedCount = 1,
        Sidecar = "not-applicable",
        Commit = commit,
        Error = "The plugin commit is rejected.",
    };

    private static SkillSyncResult.SourceRow PluginFailure(string name) => new()
    {
        Name = name,
        FailedCount = 1,
        Sidecar = "not-applicable",
        Error = "The source sync failed. Existing files remain in use.",
    };

    private async Task<SkillSyncResult.SourceRow> SyncFeedAsync(SkillFeedSource feed, CancellationToken cancellationToken)
    {
        var feedDir = _paths.ServerFeedDirectory(feed.Name);
        Directory.CreateDirectory(feedDir);

        var syncState = SkillSyncHelpers.ReadSyncState(
            _paths.ServerFeedSyncStatePath(feed.Name), _logger);
        var now = _timeProvider.GetUtcNow();
        var changedCount = 0;
        var unchangedCount = 0;
        var rejectedCount = 0;
        var failedCount = 0;

        RfcSkillIndex? index;
        using var client = _clientFactory(feed);
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(feed.TimeoutSeconds));
            try
            {
                index = await client.GetRfcIndexAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Server feed '{FeedName}' RFC index fetch timed out — using on-disk skills",
                    feed.Name);
                return SourceFailure(feed.Name, "not-run", "The RFC index request timed out.");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Server feed '{FeedName}' RFC index fetch failed: {Message} — using on-disk skills",
                    feed.Name, ex.Message);
                return SourceFailure(feed.Name, "not-run", "The RFC index request failed.");
            }
        }

        if (index is null)
        {
            _logger.LogDebug("Server feed '{FeedName}' returned no RFC index", feed.Name);
            return SourceFailure(feed.Name, "not-run", "The RFC index response was unusable.");
        }

        if (index.Skills.Count == 0)
        {
            _logger.LogDebug("Server feed '{FeedName}' returned empty index", feed.Name);
        }
        else
        {
            _logger.LogDebug(
                "Fetched RFC index from server feed '{FeedName}' ({SkillCount} skills)",
                feed.Name, index.Skills.Count);
        }

        var updated = false;

        foreach (var entry in index.Skills)
        {
            var digestHex = NormalizeDigest(entry.Digest);
            var version = entry.Version ?? "unknown";

            if (syncState.Skills.TryGetValue(entry.Name, out var existing)
                && existing.Version == version
                && string.Equals(existing.Sha256, digestHex, StringComparison.OrdinalIgnoreCase))
            {
                unchangedCount++;
                continue;
            }

            try
            {
                List<DownloadedSkillFile>? downloadedFiles;
                if (string.Equals(entry.Type, ArchiveType, StringComparison.OrdinalIgnoreCase))
                {
                    var archiveBytes = await DownloadAndVerifyBytesAsync(
                        client, entry.Url, digestHex, entry.Name, feed.TimeoutSeconds, cancellationToken);
                    if (archiveBytes is null)
                    {
                        failedCount++;
                        continue;
                    }

                    downloadedFiles = await ExtractArchiveAsync(entry.Name, feed.Name, archiveBytes, cancellationToken);
                    if (downloadedFiles is null)
                    {
                        rejectedCount++;
                        continue;
                    }
                }
                else
                {
                    var mainContent = await DownloadAndVerifyAsync(
                        client, entry.Url, digestHex, entry.Name, feed.TimeoutSeconds, cancellationToken);
                    if (mainContent is null)
                    {
                        failedCount++;
                        continue;
                    }

                    var mainScan = await _scanner.ScanAsync(entry.Name, mainContent, cancellationToken);
                    if (!mainScan.IsAllowed)
                    {
                        _logger.LogWarning(
                            "Rejected skill '{SkillName}' from feed '{FeedName}': {Reason}",
                            entry.Name, feed.Name, mainScan.Reason);
                        rejectedCount++;
                        continue;
                    }

                    downloadedFiles = new List<DownloadedSkillFile>
                    {
                        new(SkillFileName, mainContent)
                    };

                    if (entry.Resources is { Count: > 0 })
                    {
                        var allFilesOk = true;
                        foreach (var resource in entry.Resources)
                        {
                            var normalizedPath = SkillSyncHelpers.ValidateResourcePath(resource.Path);
                            if (normalizedPath is null)
                            {
                                _logger.LogWarning(
                                    "Rejected resource path for '{SkillName}' from feed '{FeedName}': {Path}",
                                    entry.Name, feed.Name, resource.Path);
                                allFilesOk = false;
                                rejectedCount++;
                                break;
                            }

                            var resourceDigest = NormalizeDigest(resource.Digest);
                            var fileContent = await DownloadAndVerifyAsync(
                                client, resource.Url, resourceDigest,
                                $"{entry.Name}/{resource.Path}", feed.TimeoutSeconds, cancellationToken);
                            if (fileContent is null)
                            {
                                allFilesOk = false;
                                failedCount++;
                                break;
                            }

                            var fileScan = await _scanner.ScanAsync(
                                $"{entry.Name}:{normalizedPath}", fileContent, cancellationToken);
                            if (!fileScan.IsAllowed)
                            {
                                _logger.LogWarning(
                                    "Rejected resource for '{SkillName}' from feed '{FeedName}' at {Path}: {Reason}",
                                    entry.Name, feed.Name, normalizedPath, fileScan.Reason);
                                allFilesOk = false;
                                rejectedCount++;
                                break;
                            }

                            downloadedFiles.Add(new DownloadedSkillFile(normalizedPath, fileContent));
                        }

                        if (!allFilesOk)
                            continue;
                    }
                }

                await SkillSyncHelpers.ReplaceSkillDirectoryAsync(
                    feedDir, entry.Name, downloadedFiles, cancellationToken);

                syncState.Skills[entry.Name] = new SyncedSkillState
                {
                    Version = version,
                    Sha256 = digestHex,
                    SyncedAtUtc = now
                };

                _logger.LogInformation(
                    "Synced skill '{SkillName}' v{Version} from feed '{FeedName}'",
                    entry.Name, version, feed.Name);
                updated = true;
                changedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to sync skill '{SkillName}' from feed '{FeedName}' — keeping existing version",
                    entry.Name, feed.Name);
                failedCount++;
            }
        }

        if (index.Skills.Count > 0)
        {
            // Reverse pass: drop skills the server no longer advertises. This is
            // only reached with a confirmed, non-empty index, so a transient outage
            // or empty response never triggers a skill prune.
            var serverSkillNames = index.Skills.Select(e => e.Name).ToList();
            var pruneResult = SkillSyncHelpers.PruneRemovedSkills(feedDir, serverSkillNames, syncState, _logger);
            if (pruneResult.Changed)
            {
                updated = true;
                changedCount += pruneResult.RemovedCount;
            }

            failedCount += pruneResult.FailedCount;
        }

        if (updated)
        {
            syncState.LastSyncUtc = now;
            SkillSyncHelpers.WriteSyncState(
                _paths.ServerFeedSyncStatePath(feed.Name), syncState);
        }

        var sidecar = await SyncNativeSubAgentsAsync(feed, client, now, cancellationToken);
        if (sidecar == "failed")
            failedCount++;

        return new SkillSyncResult.SourceRow
        {
            Name = feed.Name,
            ChangedCount = changedCount,
            UnchangedCount = unchangedCount,
            RejectedCount = rejectedCount,
            FailedCount = failedCount,
            Sidecar = sidecar,
            Error = failedCount > 0 ? "One or more source items failed." : null,
        };
    }

    private async Task<string> SyncNativeSubAgentsAsync(
        SkillFeedSource feed,
        SkillServerClient client,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!SkillSyncHelpers.IsSafeManagedFileStem(feed.Name))
        {
            _logger.LogWarning(
                "Skipping native sub-agent sync for feed '{FeedName}': unsafe managed feed directory name",
                feed.Name);
            return "failed";
        }

        var feedDir = _paths.ServerFeedAgentDirectory(feed.Name);
        var syncState = SkillSyncHelpers.ReadSyncState(
            _paths.ServerFeedAgentSyncStatePath(feed.Name), _logger);

        NativeSubAgentCollectionIndex? subAgentIndex;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(feed.TimeoutSeconds));

            subAgentIndex = await client.GetNativeSubAgentIndexAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Server feed '{FeedName}' native sidecar fetch timed out — keeping managed sub-agents",
                feed.Name);
            return "failed";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(
                "Server feed '{FeedName}' native sidecar fetch failed: {Message} — keeping managed sub-agents",
                feed.Name, ex.Message);
            return ex.StatusCode == System.Net.HttpStatusCode.NotFound ? "absent" : "failed";
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Server feed '{FeedName}' native sidecar is malformed — keeping managed sub-agents",
                feed.Name);
            return "failed";
        }

        if (subAgentIndex is null)
            return "absent";

        var advertisedNames = new List<string>();
        var changed = false;
        var fullySuccessful = true;

        foreach (var pageLink in subAgentIndex.Pages)
        {
            NativeSubAgentCollectionPage? page;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(feed.TimeoutSeconds));
                page = await client.GetNativeSubAgentPageAsync(pageLink, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch native sub-agent page {Href} from feed '{FeedName}' — keeping existing managed sub-agents",
                    pageLink.Href, feed.Name);
                fullySuccessful = false;
                continue;
            }

            if (page is null)
            {
                fullySuccessful = false;
                continue;
            }

            foreach (var item in page.Items)
            {
                advertisedNames.Add(item.Name);
                var result = await SyncNativeSubAgentAsync(
                    feed, client, feedDir, item, syncState, now, cancellationToken);
                changed |= result.Changed;
                fullySuccessful &= result.Success;
            }
        }

        if (fullySuccessful
            && SkillSyncHelpers.PruneRemovedSubAgents(feedDir, advertisedNames, syncState, _logger))
        {
            changed = true;
        }

        if (changed)
        {
            syncState.LastSyncUtc = now;
            SkillSyncHelpers.WriteSyncState(
                _paths.ServerFeedAgentSyncStatePath(feed.Name), syncState);
        }

        return fullySuccessful ? "complete" : "failed";
    }

    private async Task<NativeSubAgentSyncResult> SyncNativeSubAgentAsync(
        SkillFeedSource feed,
        SkillServerClient client,
        string feedDir,
        NativeSubAgentPageItem item,
        SkillSyncState syncState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!SkillSyncHelpers.IsSafeManagedFileStem(item.Name))
        {
            _logger.LogWarning(
                "Rejected native sub-agent '{AgentName}' from feed '{FeedName}': unsafe managed file name",
                item.Name, feed.Name);
            return NativeSubAgentSyncResult.Failed;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(feed.TimeoutSeconds));

            var identity = await client.GetNativeSubAgentIdentityAsync(item, cts.Token);
            var versionLink = SelectSubAgentVersion(item, identity);
            if (versionLink is null)
            {
                _logger.LogWarning(
                    "Native sub-agent '{AgentName}' from feed '{FeedName}' has no latest version link",
                    item.Name, feed.Name);
                return NativeSubAgentSyncResult.Failed;
            }

            var detail = await client.GetNativeSubAgentVersionAsync(versionLink, cts.Token);
            if (detail is null)
                return NativeSubAgentSyncResult.Failed;

            if (!string.Equals(detail.Type, SubAgentArtifactTypes.AgentMd, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Native sub-agent '{AgentName}' from feed '{FeedName}' has unsupported artifact type '{ArtifactType}'",
                    item.Name, feed.Name, detail.Type);
                return NativeSubAgentSyncResult.Failed;
            }

            if (!string.Equals(detail.Name, item.Name, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Native sub-agent '{AgentName}' from feed '{FeedName}' returned mismatched detail name '{DetailName}'",
                    item.Name, feed.Name, detail.Name);
                return NativeSubAgentSyncResult.Failed;
            }

            var digestHex = NormalizeDigest(detail.Digest);
            var targetFileName = $"{item.Name}.md";
            var targetPath = Path.Combine(feedDir, targetFileName);
            if (syncState.Skills.TryGetValue(item.Name, out var existing)
                && existing.Version == detail.Version
                && string.Equals(existing.Sha256, digestHex, StringComparison.OrdinalIgnoreCase)
                && File.Exists(targetPath))
            {
                return NativeSubAgentSyncResult.Unchanged;
            }

            await using var stream = new MemoryStream();
            var download = await client.DownloadNativeSubAgentArtifactAsync(detail, stream, cts.Token);
            if (!string.Equals(NormalizeDigest(download.Digest), digestHex, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Native sub-agent '{AgentName}' from feed '{FeedName}' verified unexpected digest {ActualDigest}",
                    item.Name, feed.Name, download.Digest);
                return NativeSubAgentSyncResult.Failed;
            }

            var contentBytes = stream.ToArray();
            string content;
            try
            {
                content = StrictUtf8.GetString(contentBytes);
            }
            catch (DecoderFallbackException ex)
            {
                _logger.LogWarning(
                    "Rejected native sub-agent '{AgentName}' from feed '{FeedName}': agent.md is not valid UTF-8: {Message}",
                    item.Name, feed.Name, ex.Message);
                return NativeSubAgentSyncResult.Failed;
            }

            var profile = FileSubAgentDefinitionLoader.TryParseDefinition(targetPath, content, _logger);
            if (profile is null)
                return NativeSubAgentSyncResult.Failed;

            if (!string.Equals(profile.Name, item.Name, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Rejected native sub-agent '{AgentName}' from feed '{FeedName}': artifact frontmatter name was '{FrontmatterName}'",
                    item.Name, feed.Name, profile.Name);
                return NativeSubAgentSyncResult.Failed;
            }

            await SkillSyncHelpers.ReplaceFileAsync(
                feedDir, targetFileName, contentBytes, cancellationToken);

            syncState.Skills[item.Name] = new SyncedSkillState
            {
                Version = detail.Version,
                Sha256 = digestHex,
                SyncedAtUtc = now
            };

            _logger.LogInformation(
                "Synced sub-agent '{AgentName}' v{Version} from feed '{FeedName}'",
                item.Name, detail.Version, feed.Name);
            return NativeSubAgentSyncResult.Updated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Failed to sync native sub-agent '{AgentName}' from feed '{FeedName}' — keeping existing version",
                item.Name, feed.Name);
            return NativeSubAgentSyncResult.Failed;
        }
    }

    private static NativeSubAgentVersionLink? SelectSubAgentVersion(
        NativeSubAgentPageItem item,
        NativeSubAgentIdentityIndex? identity)
    {
        if (identity is null)
            return null;

        var latestVersion = string.IsNullOrWhiteSpace(item.LatestVersion)
            ? identity.LatestVersion
            : item.LatestVersion;
        if (string.IsNullOrWhiteSpace(latestVersion))
            return identity.Versions.FirstOrDefault();

        return identity.Versions.FirstOrDefault(v =>
            string.Equals(v.Version, latestVersion, StringComparison.Ordinal));
    }

    private static SkillServerClient CreateSkillServerClient(SkillFeedSource feed)
        => new(feed.Url, feed.ApiKey?.Value);

    private async Task<string?> DownloadAndVerifyAsync(
        SkillServerClient client, string url, string expectedSha256Hex, string label,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var contentBytes = await DownloadAndVerifyBytesAsync(
            client, url, expectedSha256Hex, label, timeoutSeconds, cancellationToken);
        if (contentBytes is null)
            return null;

        try
        {
            return StrictUtf8.GetString(contentBytes);
        }
        catch (DecoderFallbackException ex)
        {
            _logger.LogWarning("Downloaded content for {Label} is not valid UTF-8: {Message}", label, ex.Message);
            return null;
        }
    }

    private async Task<byte[]?> DownloadAndVerifyBytesAsync(
        SkillServerClient client, string url, string expectedSha256Hex, string label,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var bytes = await client.DownloadVerifiedArtifactBytesAsync(
                url, expectedSha256Hex, cts.Token);
            return bytes;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Download timed out for {Label}", label);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Download failed for {Label}: {Message}", label, ex.Message);
            return null;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning("Download failed verification for {Label}: {Message}", label, ex.Message);
            return null;
        }
    }

    internal async Task<List<DownloadedSkillFile>?> ExtractArchiveAsync(
        string skillName,
        string feedName,
        byte[] archiveBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream(archiveBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<DownloadedSkillFile>();
            var hasSkillFile = false;

            foreach (var entry in archive.Entries)
            {
                var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                    || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
                var normalizedPath = NormalizeArchiveEntryPath(entry.FullName, isDirectory);
                if (normalizedPath is null)
                {
                    _logger.LogWarning(
                        "Rejected archive for skill '{SkillName}' from feed '{FeedName}': unsafe entry path '{Path}'",
                        skillName, feedName, entry.FullName);
                    return null;
                }

                if (IsZipSymlink(entry))
                {
                    _logger.LogWarning(
                        "Rejected archive for skill '{SkillName}' from feed '{FeedName}': symlink entry '{Path}'",
                        skillName, feedName, normalizedPath);
                    return null;
                }

                if (isDirectory)
                    continue;

                if (!paths.Add(normalizedPath))
                {
                    _logger.LogWarning(
                        "Rejected archive for skill '{SkillName}' from feed '{FeedName}': duplicate entry '{Path}'",
                        skillName, feedName, normalizedPath);
                    return null;
                }

                using var entryStream = entry.Open();
                using var entryBytes = new MemoryStream();
                await entryStream.CopyToAsync(entryBytes, cancellationToken);
                var content = entryBytes.ToArray();
                var unixMode = GetUnixMode(entry);
                if (unixMode is { } mode && (mode & ~0x1FF) != 0)
                {
                    _logger.LogWarning(
                        "Rejected archive for skill '{SkillName}' from feed '{FeedName}': unsupported Unix mode {UnixMode} on '{Path}'",
                        skillName, feedName, mode, normalizedPath);
                    return null;
                }

                if (string.Equals(normalizedPath, SkillFileName, StringComparison.OrdinalIgnoreCase))
                {
                    hasSkillFile = true;
                    string skillContent;
                    try
                    {
                        skillContent = StrictUtf8.GetString(content);
                    }
                    catch (DecoderFallbackException ex)
                    {
                        _logger.LogWarning(
                            "Rejected archive for skill '{SkillName}' from feed '{FeedName}': SKILL.md is not valid UTF-8: {Message}",
                            skillName, feedName, ex.Message);
                        return null;
                    }

                    var mainScan = await _scanner.ScanAsync(skillName, skillContent, cancellationToken);
                    if (!mainScan.IsAllowed)
                    {
                        _logger.LogWarning(
                            "Rejected archive skill '{SkillName}' from feed '{FeedName}': {Reason}",
                            skillName, feedName, mainScan.Reason);
                        return null;
                    }
                }
                else if (TryDecodeUtf8(content, out var textContent))
                {
                    var fileScan = await _scanner.ScanAsync(
                        $"{skillName}:{normalizedPath}", textContent, cancellationToken);
                    if (!fileScan.IsAllowed)
                    {
                        _logger.LogWarning(
                            "Rejected archive resource for '{SkillName}' from feed '{FeedName}' at {Path}: {Reason}",
                            skillName, feedName, normalizedPath, fileScan.Reason);
                        return null;
                    }
                }

                files.Add(new DownloadedSkillFile(normalizedPath, content, unixMode));
            }

            if (!hasSkillFile)
            {
                _logger.LogWarning(
                    "Rejected archive for skill '{SkillName}' from feed '{FeedName}': missing SKILL.md",
                    skillName, feedName);
                return null;
            }

            return files
                .OrderBy(static file => string.Equals(file.RelativePath, SkillFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(static file => file.RelativePath, StringComparer.Ordinal)
                .ToList();
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(
                "Rejected archive for skill '{SkillName}' from feed '{FeedName}': invalid ZIP archive: {Message}",
                skillName, feedName, ex.Message);
            return null;
        }
    }

    private static string? NormalizeArchiveEntryPath(string path, bool isDirectory)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (isDirectory)
            normalized = normalized.TrimEnd('/');

        if (string.Equals(normalized, SkillFileName, StringComparison.OrdinalIgnoreCase))
            return SkillFileName;

        return SkillSyncHelpers.ValidateResourcePath(normalized);
    }

    private static bool TryDecodeUtf8(byte[] content, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool IsZipSymlink(ZipArchiveEntry entry)
        => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static int? GetUnixMode(ZipArchiveEntry entry)
    {
        var mode = (entry.ExternalAttributes >> 16) & 0xFFF;
        return mode == 0 ? null : mode;
    }

    private MergedSkillScanResult RescanAndUpdateIndex(
        IReadOnlyList<ResolvedExternalSource> managedGitPluginSources)
    {
        var mergedResult = _inventoryRefresher.ReplaceManagedGitPluginSourcesAndRefresh(
            managedGitPluginSources);

        if (mergedResult.Issues.Count > 0)
        {
            _logger.LogWarning(
                "Skill inventory is degraded after server feed sync: accepted={AcceptedSkillCount} rejected={RejectedIssueCount}",
                mergedResult.AcceptedSkills.Count, mergedResult.Issues.Count);

            foreach (var issue in mergedResult.Issues)
            {
                _logger.LogWarning(
                    "Rejected skill item during server feed sync rebuild: kind={IssueKind} path={Path} message={Message}",
                    issue.Kind, issue.Path, issue.Message);
            }
        }
        else
        {
            _logger.LogInformation(
                "Skill index updated after server feed sync ({SkillCount} skills)",
                mergedResult.AcceptedSkills.Count);
        }

        return mergedResult;
    }

    private static SkillSyncResult.SourceRow SourceFailure(
        string name,
        string sidecar,
        string error) => new()
        {
            Name = name,
            FailedCount = 1,
            Sidecar = sidecar,
            Error = error,
        };

    internal static string NormalizeDigest(string digest)
    {
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return digest[7..];
        return digest;
    }

    private readonly record struct NativeSubAgentSyncResult(bool Success, bool Changed)
    {
        public static NativeSubAgentSyncResult Failed => new(false, false);
        public static NativeSubAgentSyncResult Unchanged => new(true, false);
        public static NativeSubAgentSyncResult Updated => new(true, true);
    }
}
