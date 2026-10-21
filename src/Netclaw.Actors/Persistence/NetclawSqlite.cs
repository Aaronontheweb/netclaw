// -----------------------------------------------------------------------
// <copyright file="NetclawSqlite.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;

namespace Netclaw.Actors.Persistence;

/// <summary>
/// The one place where Netclaw code creates a connection to netclaw.db.
/// </summary>
/// <remarks>
/// STUB from the test author. This method still returns a plain connection, which runs with
/// <c>synchronous=FULL</c>. The implementer replaces the body so that every connection runs with
/// <c>synchronous=NORMAL</c>, and then moves all production <c>new SqliteConnection(</c> sites to it.
/// </remarks>
public static class NetclawSqlite
{
    public static SqliteConnection CreateConnection(string connectionString)
        => new(connectionString);
}
