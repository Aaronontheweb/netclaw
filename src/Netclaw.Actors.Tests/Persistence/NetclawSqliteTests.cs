// -----------------------------------------------------------------------
// <copyright file="NetclawSqliteTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Persistence;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Persistence;

/// <summary>
/// Production incident, Oct 2026: every SQLite commit ran with the default <c>synchronous=FULL</c>.
/// In WAL mode that setting syncs the WAL file while the commit holds the database write lock. A slow
/// disk sync then blocked every other writer. <c>synchronous=NORMAL</c> (value 1) skips that sync on
/// commit. The setting belongs to one connection, so a single FULL writer still holds the lock across a sync.
/// </summary>
public sealed class NetclawSqliteTests : IDisposable
{
    private const long SynchronousNormal = 1;

    private readonly DisposableTempDir _directory = new();

    private string DbPath => Path.Combine(_directory.Path, "netclaw.db");

    public void Dispose()
    {
        SqliteMemoryStoreTestPools.ClearOwnedDirectory(_directory.Path);
        _directory.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_shared_seam_opens_connections_with_synchronous_normal(bool openAsync)
    {
        // Pooling is off so that the new native connection, not a pooled one, carries the setting.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        await using var connection = NetclawSqlite.CreateConnection(connectionString);
        if (openAsync)
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        else
            connection.Open();

        Assert.Equal(SynchronousNormal, ReadPragma(connection, "synchronous"));
    }

    // The memory store is the first writer to netclaw.db and it owns the WAL setting.
    [Fact]
    public async Task The_memory_store_keeps_the_database_in_wal_mode()
    {
        var store = new SQLiteMemoryStore(DbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await using var probe = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        await probe.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("wal", ReadPragma(probe, "journal_mode"));
    }

    // Wiring guard. The store keeps its connections in the connection pool of its exact connection string.
    // A probe with the same string gets a pooled native connection that the store opened.
    [Fact]
    public async Task The_memory_store_runs_its_connections_with_synchronous_normal()
    {
        var store = new SQLiteMemoryStore(DbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await using var probe = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        await probe.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SynchronousNormal, ReadPragma(probe, "synchronous"));
    }

    internal static object? ReadPragma(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma}";
        return command.ExecuteScalar();
    }
}
