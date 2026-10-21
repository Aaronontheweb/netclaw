// -----------------------------------------------------------------------
// <copyright file="SqliteSynchronousNormalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Tests.Utilities;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Every writer to netclaw.db must run with <c>synchronous=NORMAL</c> (value 1). See
/// <c>NetclawSqliteTests</c> in Netclaw.Actors.Tests for the reason. The Akka SQL plugin opens its own
/// connections, so the daemon persistence configuration must set the value for them.
/// </summary>
public sealed class DaemonPersistenceSynchronousTests(ITestOutputHelper output) : DaemonPersistenceTestBase(output)
{
    internal const long SynchronousNormal = 1;

    // The plugin keeps its connections in the pool of the exact connection string. After the plugin has
    // written, a probe with the same string gets a pooled native connection that the plugin opened.
    [Fact]
    public async Task Journal_and_snapshot_store_connections_run_with_synchronous_normal()
    {
        var probe = Sys.ActorOf(Props.Create(() => new PersistingProbe()), "synchronous-probe");

        Assert.Equal("persisted", await probe.Ask<string>("persist", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal("snapshotted", await probe.Ask<string>("snapshot", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous";

        Assert.Equal(SynchronousNormal, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private sealed class PersistingProbe : UntypedPersistentActor
    {
        public override string PersistenceId => "synchronous-probe";

        protected override void OnCommand(object message)
        {
            switch (message)
            {
                case "persist":
                    var replyTo = Sender;
                    Persist("event", _ => replyTo.Tell("persisted"));
                    break;
                case "snapshot":
                    SaveSnapshot("state");
                    Context.Become(WaitForSnapshot(Sender));
                    break;
            }
        }

        private UntypedReceive WaitForSnapshot(IActorRef replyTo) => message =>
        {
            if (message is SaveSnapshotSuccess)
                replyTo.Tell("snapshotted");
        };

        protected override void OnRecover(object message) { }
    }
}

/// <summary>
/// Wiring guard for the Netclaw-owned connections of the daemon. A pooled probe sees the setting that
/// the catalog service put on its connection. Cache misses of the storage resolver use no pool, so
/// <c>NetclawSqliteTests</c> covers the shared seam for it.
/// </summary>
public sealed class SessionCatalogSynchronousTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    public void Dispose()
    {
        SqliteTestPools.Clear(new NetclawPaths(_directory.Path));
        _directory.Dispose();
    }

    [Fact]
    public async Task The_session_catalog_runs_its_connections_with_synchronous_normal()
    {
        var paths = new NetclawPaths(_directory.Path);
        paths.EnsureDirectoriesExist();
        var service = new SessionCatalogService(
            paths,
            TimeProvider.System,
            new TestSessionStorageResolver(paths),
            NullLogger<SessionCatalogService>.Instance);

        _ = service.ListRecent();

        await using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await probe.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = probe.CreateCommand();
        command.CommandText = "PRAGMA synchronous";

        Assert.Equal(DaemonPersistenceSynchronousTests.SynchronousNormal, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
