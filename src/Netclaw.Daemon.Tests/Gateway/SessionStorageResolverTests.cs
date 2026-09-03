// -----------------------------------------------------------------------
// <copyright file="SessionStorageResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class SessionStorageResolverTests : IDisposable
{
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(),
        $"netclaw-session-storage-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    [Fact]
    public async Task Concurrent_first_consumers_receive_one_persisted_envelope()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider());
        var sessionId = new SessionId("signalr/new-session");

        var resolutions = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => resolver.Resolve(sessionId))));

        var root = Assert.IsType<SessionStorageBinding>(resolutions[0].Binding).EnvelopeRoot;
        Assert.All(resolutions, result => Assert.Equal(root, result.Binding?.EnvelopeRoot));
        Assert.Equal(1, CountBindings(paths));
    }

    [Fact]
    public async Task Persisted_binding_wins_after_the_configured_sessions_root_changes()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider());
        var sessionId = new SessionId("signalr/stable-session");
        var first = resolver.Resolve(sessionId);
        var alternateSessionsRoot = Path.Combine(_basePath, "alternate-sessions");

        var second = new SqliteSessionStorageResolver(
            paths,
            new FakeTimeProvider(),
            alternateSessionsRoot).Resolve(sessionId);

        Assert.Equal(first.Binding, second.Binding);
        Assert.False(second.SessionDirectory.StartsWith(alternateSessionsRoot, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Existing_session_keeps_legacy_paths_and_receives_no_binding()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var sessionId = new SessionId("signalr/existing-session");
        var legacyDirectory = SessionDirectoryHelper.GetSessionDirectory(sessionId, paths.SessionsDirectory);
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "existing.txt"), "keep");

        var storage = new SqliteSessionStorageResolver(paths, new FakeTimeProvider()).Resolve(sessionId);

        Assert.Null(storage.Binding);
        Assert.Equal(legacyDirectory, storage.SessionDirectory);
        Assert.True(File.Exists(Path.Combine(legacyDirectory, "existing.txt")));
        Assert.Equal(0, CountBindings(paths));
    }

    [Fact]
    public async Task Distinct_session_ids_with_the_same_display_form_get_distinct_envelopes()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider());

        var first = resolver.Resolve(new SessionId("channel/a_b"));
        var second = resolver.Resolve(new SessionId("channel/a/b"));

        Assert.NotEqual(first.Binding?.EnvelopeRoot, second.Binding?.EnvelopeRoot);
        Assert.Equal(2, CountBindings(paths));
    }

    [Fact]
    public async Task Journal_only_session_keeps_legacy_paths_and_receives_no_binding()
    {
        var paths = CreatePaths();
        var sessionId = new SessionId("signalr/journal-only");
        await MigrateAsync(paths, paths.SqliteDbPath);
        using (var connection = new SqliteConnection($"Data Source={paths.SqliteDbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO journal(deleted, persistence_id, sequence_number, created, message)
                VALUES (0, $persistenceId, 1, 0, X'00');
                """;
            command.Parameters.AddWithValue("$persistenceId", $"session-{sessionId.Value}");
            command.ExecuteNonQuery();
        }

        var storage = new SqliteSessionStorageResolver(paths, new FakeTimeProvider()).Resolve(sessionId);

        Assert.Null(storage.Binding);
        Assert.Equal(
            SessionDirectoryHelper.GetSessionDirectory(sessionId, paths.SessionsDirectory),
            storage.SessionDirectory);
        Assert.Equal(0, CountBindings(paths));
    }

    [Fact]
    public async Task Repeated_resolution_uses_the_cached_immutable_result()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider());
        var sessionId = new SessionId("signalr/cached-session");

        var first = resolver.Resolve(sessionId);
        var second = resolver.Resolve(sessionId);

        Assert.Same(first, second);
        Assert.Equal(1, CountBindings(paths));
    }

    [Fact]
    public async Task Configured_persistence_database_owns_journal_and_storage_bindings()
    {
        var paths = CreatePaths();
        var configuredDatabase = Path.Combine(_basePath, "persistence", "custom.db");
        await MigrateAsync(paths, configuredDatabase);
        var options = new DaemonPersistenceOptions
        {
            Sqlite = new SqlitePersistenceOptions { Path = configuredDatabase }
        };
        var sessionId = new SessionId("signalr/custom-database");

        using (var connection = new SqliteConnection($"Data Source={configuredDatabase}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO journal(deleted, persistence_id, sequence_number, created, message)
                VALUES (0, $persistenceId, 1, 0, X'00');
                """;
            command.Parameters.AddWithValue("$persistenceId", $"session-{sessionId.Value}");
            command.ExecuteNonQuery();
        }

        var legacy = new SqliteSessionStorageResolver(
            paths,
            new FakeTimeProvider(),
            options,
            paths.SessionsDirectory).Resolve(sessionId);
        var freshSession = new SessionId("signalr/custom-database-new");
        var fresh = new SqliteSessionStorageResolver(
            paths,
            new FakeTimeProvider(),
            options,
            paths.SessionsDirectory).Resolve(freshSession);

        Assert.Null(legacy.Binding);
        Assert.NotNull(fresh.Binding);
        Assert.Equal(1, CountBindings(configuredDatabase));
    }

    private NetclawPaths CreatePaths()
    {
        var paths = new NetclawPaths(_basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static long CountBindings(NetclawPaths paths)
        => CountBindings(paths.SqliteDbPath);

    private static long CountBindings(string sqlitePath)
    {
        using var connection = new SqliteConnection($"Data Source={sqlitePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_storage_bindings";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static Task MigrateAsync(NetclawPaths paths, string sqlitePath)
        => new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(sqlitePath, TestContext.Current.CancellationToken);
}
