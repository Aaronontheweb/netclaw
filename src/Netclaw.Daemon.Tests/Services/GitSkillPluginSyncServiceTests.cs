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

    public void Dispose() => _temp.Dispose();

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
    public async Task Startup_cleanup_removes_staging_and_orphan_commits_but_keeps_receipt_directory()
    {
        var source = Source();
        var store = await CreateStoreAsync();
        await store.SaveReceiptAsync(source, FirstCommit, "1.0.0", TestContext.Current.CancellationToken);
        var fingerprint = GitSkillPluginSourceValidator.Fingerprint(source);
        var selected = _paths.ManagedGitSkillCommitDirectory(source.Name, fingerprint, FirstCommit);
        var orphan = _paths.ManagedGitSkillCommitDirectory(source.Name, fingerprint, SecondCommit);
        var staging = Path.Combine(_paths.ManagedGitSkillDirectory(source.Name), ".staging", "candidate");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(staging);
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
        FakeAcquirer acquirer,
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
}
