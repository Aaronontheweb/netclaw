// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginSyncServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class GitSkillPluginSyncServiceTests : IDisposable
{
    private const string FirstCommit = "13e26d39ed01d97ea592235d041304d289f4ba07";
    private const string SecondCommit = "23e26d39ed01d97ea592235d041304d289f4ba08";
    private readonly DisposableTempDir _temp = new();
    private readonly NetclawPaths _paths;
    private readonly FakeTimeProvider _time = new();

    public GitSkillPluginSyncServiceTests()
    {
        _paths = new NetclawPaths(_temp.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        SqliteTestPools.Clear(_paths);
        _temp.Dispose();
    }

    [Fact]
    public async Task First_install_publishes_all_skills_in_one_inventory_snapshot()
    {
        var source = Source();
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill");
        var registry = new SkillRegistry();
        var publicationCount = 0;
        var refresher = CreateRefresher(registry, () => publicationCount++);
        var service = await CreateServiceAsync(source, refresher, acquirer, new RecordingSink());

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, publicationCount);
        Assert.Equal(FirstCommit, Assert.Single(result.Sources).Commit);
        Assert.NotNull(registry.GetByName("plugin-skill"));
    }

    [Fact]
    public async Task Branch_update_keeps_prior_directory_and_publishes_new_files()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var first = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill", "old text");
        var service = await CreateServiceAsync(source, refresher, first, new RecordingSink());
        await service.SyncAsync(TestContext.Current.CancellationToken);
        var oldPath = registry.GetByName("plugin-skill")!.FilePath;

        var second = new FakeAcquirer(_paths, source, SecondCommit, "2.0.0", "plugin-skill", "new text");
        var nextService = await CreateServiceAsync(source, refresher, second, new RecordingSink());
        await nextService.SyncAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(oldPath));
        Assert.Contains("old text", await File.ReadAllTextAsync(oldPath, TestContext.Current.CancellationToken));
        Assert.Contains("new text", await File.ReadAllTextAsync(
            registry.GetByName("plugin-skill")!.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Equal_version_records_last_observed_commit_without_a_second_fetch()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var first = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill");
        var service = await CreateServiceAsync(source, refresher, first, new RecordingSink());
        await service.SyncAsync(TestContext.Current.CancellationToken);

        var next = new FakeAcquirer(_paths, source, SecondCommit, "1.0.0", "plugin-skill");
        var nextService = await CreateServiceAsync(source, refresher, next, new RecordingSink());
        await nextService.SyncAsync(TestContext.Current.CancellationToken);
        await nextService.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, next.AcquireCount);
        var receipt = await new GitSkillPluginStateStore(_paths, _time).GetReceiptAsync(
            source.Name, TestContext.Current.CancellationToken);
        Assert.NotNull(receipt);
        Assert.Equal(FirstCommit, receipt.InstalledCommit);
        Assert.Equal(SecondCommit, receipt.LastObservedCommit);
        Assert.False(Directory.Exists(_paths.ManagedGitSkillCommitDirectory(
            source.Name, GitSkillPluginSourceValidator.Fingerprint(source), SecondCommit)));
    }

    [Fact]
    public async Task Commit_pin_does_not_resolve_and_known_rejection_survives_a_new_service()
    {
        var source = Source();
        source.ReferenceKind = GitSkillPluginReferenceKind.Commit;
        source.Reference = FirstCommit.ToUpperInvariant();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var store = await CreateStoreAsync();
        await store.SaveRejectionAsync(
            source.Name,
            GitSkillPluginSourceValidator.Fingerprint(source),
            FirstCommit,
            "unsafe plugin",
            false,
            TestContext.Current.CancellationToken);
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill")
        {
            FailOnResolve = true,
        };
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] },
            _paths,
            refresher,
            _time,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            store,
            acquirer,
            new RecordingSink());

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, acquirer.ResolveCount);
        Assert.Equal(0, acquirer.AcquireCount);
        Assert.Equal(1, Assert.Single(result.Sources).RejectedCount);
    }

    [Fact]
    public async Task Security_rejection_persists_and_emits_one_claimed_alert()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill")
        {
            Rejection = new GitSkillPluginRejectedException(FirstCommit, "scanner result", true),
        };
        var alerts = new RecordingSink();
        var service = await CreateServiceAsync(source, refresher, acquirer, alerts);

        await service.SyncAsync(TestContext.Current.CancellationToken);
        await service.SyncAsync(TestContext.Current.CancellationToken);

        var alert = Assert.Single(alerts.Alerts);
        Assert.Equal("skill.plugin.security_rejected", alert.Type);
        Assert.Equal(AlertType.SkillPluginSecurityRejected, alert.Category);
        Assert.DoesNotContain("scanner result", alert.Summary);
    }

    [Fact]
    public async Task Restart_claims_and_emits_a_persisted_security_rejection_alert()
    {
        var source = Source();
        var store = await CreateStoreAsync();
        var fingerprint = GitSkillPluginSourceValidator.Fingerprint(source);
        await store.SaveRejectionAsync(
            source.Name, fingerprint, FirstCommit, "scanner result", true, TestContext.Current.CancellationToken);
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill");
        var alerts = new RecordingSink();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] }, _paths, CreateRefresher(new SkillRegistry(), static () => { }), _time,
            new NoOpSkillContentScanner(), NullLogger<ServerFeedSkillSyncService>.Instance, store, acquirer, alerts);

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, acquirer.AcquireCount);
        Assert.Equal("skill.plugin.security_rejected", Assert.Single(alerts.Alerts).Type);
        Assert.True((await store.GetRejectionAsync(
            source.Name, fingerprint, FirstCommit, TestContext.Current.CancellationToken))!.AlertEmitted);
    }

    [Fact]
    public async Task Source_timeout_covers_branch_resolution_before_archive_acquisition()
    {
        var source = Source();
        source.TimeoutSeconds = 1;
        var acquirer = new BlockingResolveAcquirer();
        var service = await CreateServiceAsync(
            source, CreateRefresher(new SkillRegistry(), static () => { }), acquirer, new RecordingSink());

        var sync = service.SyncAsync(TestContext.Current.CancellationToken);
        await acquirer.ResolutionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(1));
        var result = await sync;

        Assert.Equal(1, Assert.Single(result.Sources).FailedCount);
        Assert.Equal(0, acquirer.AcquireCount);
    }

    [Fact]
    public async Task Startup_cleanup_removes_staging_and_orphan_commits_but_keeps_receipt_directory()
    {
        var source = Source();
        var store = await CreateStoreAsync();
        await store.SaveReceiptAsync(source, FirstCommit, "1.0.0", TestContext.Current.CancellationToken);
        var fingerprint = GitSkillPluginSourceValidator.Fingerprint(source);
        var selected = _paths.ManagedGitSkillCommitDirectory(source.Name, fingerprint, FirstCommit);
        var orphan = _paths.ManagedGitSkillCommitDirectory(source.Name, fingerprint, SecondCommit);
        var staging = Path.Combine(_paths.ManagedGitSkillDirectory(source.Name), ".staging", "candidate");
        var publishedStagingResource = Path.Combine(selected, "plugin-skill", "resources", ".staging", "guide.md");
        var publishedCommitsResource = Path.Combine(selected, "plugin-skill", "resources", "commits", "guide.md");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(Path.GetDirectoryName(publishedStagingResource)!);
        Directory.CreateDirectory(Path.GetDirectoryName(publishedCommitsResource)!);
        File.WriteAllText(publishedStagingResource, "published staging resource");
        File.WriteAllText(publishedCommitsResource, "published commits resource");
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] },
            _paths,
            CreateRefresher(registry, static () => { }),
            _time,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            store,
            new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill"),
            new RecordingSink());

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(selected));
        Assert.False(Directory.Exists(orphan));
        Assert.False(Directory.Exists(staging));
        Assert.True(File.Exists(publishedStagingResource));
        Assert.True(File.Exists(publishedCommitsResource));
    }

    [Fact]
    public async Task Disabled_source_does_not_publish_a_prior_receipt()
    {
        var source = Source();
        var store = await CreateStoreAsync();
        await store.SaveReceiptAsync(source, FirstCommit, "1.0.0", TestContext.Current.CancellationToken);
        var directory = _paths.ManagedGitSkillCommitDirectory(
            source.Name, GitSkillPluginSourceValidator.Fingerprint(source), FirstCommit);
        Directory.CreateDirectory(Path.Combine(directory, "plugin-skill"));
        File.WriteAllText(Path.Combine(directory, "plugin-skill", "SKILL.md"), SkillMarkdown("plugin-skill", "hidden"));
        source.Enabled = false;
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill");
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] }, _paths, CreateRefresher(registry, static () => { }), _time,
            new NoOpSkillContentScanner(), NullLogger<ServerFeedSkillSyncService>.Instance, store, acquirer,
            new RecordingSink());

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, acquirer.ResolveCount);
        Assert.Null(registry.GetByName("plugin-skill"));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task Changed_source_acquisition_failure_keeps_the_prior_receipt_and_directory()
    {
        var original = Source();
        var store = await CreateStoreAsync();
        await store.SaveReceiptAsync(original, FirstCommit, "1.0.0", TestContext.Current.CancellationToken);
        var oldDirectory = _paths.ManagedGitSkillCommitDirectory(
            original.Name, GitSkillPluginSourceValidator.Fingerprint(original), FirstCommit);
        var oldSkillPath = Path.Combine(oldDirectory, "plugin-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(oldSkillPath)!);
        File.WriteAllText(oldSkillPath, SkillMarkdown("plugin-skill", "prior package"));
        var changed = Source();
        changed.Reference = "release";
        var acquirer = new FakeAcquirer(_paths, changed, SecondCommit, "2.0.0", "plugin-skill")
        {
            Failure = new IOException("network unavailable"),
        };
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [changed] }, _paths, CreateRefresher(registry, static () => { }), _time,
            new NoOpSkillContentScanner(), NullLogger<ServerFeedSkillSyncService>.Instance, store, acquirer,
            new RecordingSink());

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(result.Sources).FailedCount);
        Assert.Equal(FirstCommit, (await store.GetReceiptAsync(original.Name, TestContext.Current.CancellationToken))!.InstalledCommit);
        Assert.True(Directory.Exists(oldDirectory));
        Assert.Contains("prior package", await File.ReadAllTextAsync(
            registry.GetByName("plugin-skill")!.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Scanner_service_failure_creates_no_rejection_or_alert()
    {
        var source = Source();
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill")
        {
            Failure = new GitSkillPluginScannerUnavailableException("scanner unavailable"),
        };
        var alerts = new RecordingSink();
        var service = await CreateServiceAsync(source, CreateRefresher(new SkillRegistry(), static () => { }), acquirer, alerts);

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);
        var store = new GitSkillPluginStateStore(_paths, _time);

        Assert.Equal(1, Assert.Single(result.Sources).FailedCount);
        Assert.Null(await store.GetRejectionAsync(
            source.Name, GitSkillPluginSourceValidator.Fingerprint(source), FirstCommit,
            TestContext.Current.CancellationToken));
        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task Versionless_commit_change_publishes_the_new_commit()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        await (await CreateServiceAsync(
            source, refresher, new FakeAcquirer(_paths, source, FirstCommit, null, "plugin-skill"), new RecordingSink()))
            .SyncAsync(TestContext.Current.CancellationToken);
        var updated = new FakeAcquirer(_paths, source, SecondCommit, null, "plugin-skill", "new versionless content");
        await (await CreateServiceAsync(source, refresher, updated, new RecordingSink()))
            .SyncAsync(TestContext.Current.CancellationToken);

        var receipt = await new GitSkillPluginStateStore(_paths, _time).GetReceiptAsync(
            source.Name, TestContext.Current.CancellationToken);
        Assert.NotNull(receipt);
        Assert.Equal(SecondCommit, receipt.InstalledCommit);
        Assert.Equal(1, updated.AcquireCount);
        Assert.Contains("new versionless content", await File.ReadAllTextAsync(
            registry.GetByName("plugin-skill")!.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_failed_plugin_does_not_block_a_successful_plugin()
    {
        var failed = Source();
        failed.Name = "failed";
        var healthy = Source();
        healthy.Name = "healthy";
        var store = await CreateStoreAsync();
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [failed, healthy] },
            _paths,
            CreateRefresher(registry, static () => { }),
            _time,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            store,
            new TwoPluginAcquirer(_paths, failed, healthy),
            new RecordingSink());

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(result.Sources, row => row.Name == "failed").FailedCount);
        Assert.Equal(1, Assert.Single(result.Sources, row => row.Name == "healthy").ChangedCount);
        Assert.NotNull(registry.GetByName("healthy-skill"));
    }

    private async Task<GitSkillPluginStateStore> CreateStoreAsync()
    {
        await new SchemaMigrator(_paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(_paths.SqliteDbPath, TestContext.Current.CancellationToken);
        return new GitSkillPluginStateStore(_paths, _time);
    }

    private async Task<ServerFeedSkillSyncService> CreateServiceAsync(
        GitSkillPluginSource source,
        SkillInventoryRefresher refresher,
        IGitSkillPluginAcquirer acquirer,
        RecordingSink sink)
    {
        var store = await CreateStoreAsync();
        return new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] },
            _paths,
            refresher,
            _time,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            store,
            acquirer,
            sink);
    }

    private SkillInventoryRefresher CreateRefresher(SkillRegistry registry, Action publish)
        => new(
            _paths,
            new SkillFeedsConfig(),
            [],
            registry,
            new SkillIndexPublisher(registry, new SkillIndexContextLayer(), (_, _) =>
            {
                publish();
                return true;
            }));

    private static GitSkillPluginSource Source() => new()
    {
        Name = "fixture",
        Repository = "owner/repository",
        Format = "codex",
        ReferenceKind = GitSkillPluginReferenceKind.Branch,
        Reference = "main",
    };

    private sealed class FakeAcquirer(
        NetclawPaths paths,
        GitSkillPluginSource source,
        string commit,
        string? version,
        string skillName,
        string description = "plugin guidance") : IGitSkillPluginAcquirer
    {
        public bool FailOnResolve { get; init; }
        public GitSkillPluginRejectedException? Rejection { get; init; }
        public Exception? Failure { get; init; }
        public int ResolveCount { get; private set; }
        public int AcquireCount { get; private set; }

        public Task<GitSkillPluginCandidate> AcquireAsync(
            GitSkillPluginSource ignored,
            CancellationToken cancellationToken)
            => AcquireAsync(ignored, commit, cancellationToken);

        public Task<string> ResolveCommitAsync(
            GitSkillPluginSource ignored,
            CancellationToken cancellationToken)
        {
            ResolveCount++;
            if (FailOnResolve)
                throw new InvalidOperationException("The commit pin must not resolve.");
            return Task.FromResult(commit);
        }

        public Task<GitSkillPluginCandidate> AcquireAsync(
            GitSkillPluginSource ignored,
            string resolvedCommit,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            if (Failure is not null)
                throw Failure;
            if (Rejection is not null)
                throw Rejection;

            var directory = paths.ManagedGitSkillCommitDirectory(
                source.Name,
                GitSkillPluginSourceValidator.Fingerprint(source),
                resolvedCommit);
            var skillDirectory = Path.Combine(directory, skillName);
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), $$"""
                ---
                name: {{skillName}}
                description: {{description}}
                metadata:
                  version: {{version ?? ""}}
                ---

                # {{skillName}}
                """);
            return Task.FromResult(new GitSkillPluginCandidate(
                resolvedCommit,
                version,
                directory,
                SkillScanner.Scan(directory).AcceptedSkills,
                []));
        }
    }

    private sealed class RecordingSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];
        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }

    private sealed class TwoPluginAcquirer(NetclawPaths paths, GitSkillPluginSource failed, GitSkillPluginSource healthy)
        : IGitSkillPluginAcquirer
    {
        public Task<GitSkillPluginCandidate> AcquireAsync(GitSkillPluginSource source, CancellationToken cancellationToken)
            => AcquireAsync(source, source.Name == failed.Name ? FirstCommit : SecondCommit, cancellationToken);

        public Task<string> ResolveCommitAsync(GitSkillPluginSource source, CancellationToken cancellationToken)
            => Task.FromResult(source.Name == failed.Name ? FirstCommit : SecondCommit);

        public Task<GitSkillPluginCandidate> AcquireAsync(
            GitSkillPluginSource source,
            string commit,
            CancellationToken cancellationToken)
        {
            if (source.Name == failed.Name)
                throw new IOException("network unavailable");

            var directory = paths.ManagedGitSkillCommitDirectory(
                healthy.Name, GitSkillPluginSourceValidator.Fingerprint(healthy), commit);
            var skillDirectory = Path.Combine(directory, "healthy-skill");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), SkillMarkdown("healthy-skill", "healthy"));
            return Task.FromResult(new GitSkillPluginCandidate(
                commit, null, directory, SkillScanner.Scan(directory).AcceptedSkills, []));
        }
    }

    private sealed class BlockingResolveAcquirer : IGitSkillPluginAcquirer
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResolutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AcquireCount { get; private set; }

        public Task<GitSkillPluginCandidate> AcquireAsync(GitSkillPluginSource source, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public async Task<string> ResolveCommitAsync(GitSkillPluginSource source, CancellationToken cancellationToken)
        {
            ResolutionStarted.TrySetResult();
            await _never.Task.WaitAsync(cancellationToken);
            return "";
        }

        public Task<GitSkillPluginCandidate> AcquireAsync(
            GitSkillPluginSource source,
            string commit,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            throw new NotSupportedException();
        }
    }

    private static string SkillMarkdown(string name, string description) => $$"""
        ---
        name: {{name}}
        description: {{description}}
        ---

        # {{name}}
        """;
}
