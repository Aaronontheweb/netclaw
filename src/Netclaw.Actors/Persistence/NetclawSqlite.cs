// -----------------------------------------------------------------------
// <copyright file="NetclawSqlite.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Data;
using Microsoft.Data.Sqlite;

namespace Netclaw.Actors.Persistence;

/// <summary>
/// The one place where Netclaw code creates a connection to netclaw.db.
/// </summary>
/// <remarks>
/// Each connection runs <c>PRAGMA synchronous=NORMAL</c> when it opens. In WAL mode, a commit then does
/// not fsync while it holds the write lock. On a host with a shared ext4 journal, that fsync waits for
/// the journal commit of other writers. A checkpoint still syncs. A power loss or a kernel crash can lose
/// the last commits, but it cannot corrupt the database. A failed pragma throws from Open.
/// </remarks>
public static class NetclawSqlite
{
    public const string SynchronousNormalSql = "PRAGMA synchronous=NORMAL;";

    public static SqliteConnection CreateConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.StateChange += OnStateChange;
        return connection;
    }

    private static void OnStateChange(object sender, StateChangeEventArgs e)
    {
        if (e.CurrentState != ConnectionState.Open)
            return;

        using var command = ((SqliteConnection)sender).CreateCommand();
        command.CommandText = SynchronousNormalSql;
        command.ExecuteNonQuery();
    }
}
