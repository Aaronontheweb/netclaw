// -----------------------------------------------------------------------
// <copyright file="DaemonPersistenceConfiguration.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Persistence.Sql.Hosting;
using Microsoft.Data.Sqlite;

namespace Netclaw.Daemon;

/// <summary>
/// Composes the daemon SQLite persistence for Akka. Program.cs and the tests call the same method,
/// so a test reads the configuration that the daemon uses.
/// </summary>
internal static class DaemonPersistenceConfiguration
{
    /// <summary>
    /// The time that the circuit-breaker call-timeout adds to the SQLite command timeout.
    /// </summary>
    internal static readonly TimeSpan CircuitBreakerMargin = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Adds SQLite journal and snapshot-store persistence. The circuit-breaker call-timeout of each
    /// store is longer than the SQLite command timeout of <paramref name="connectionString"/>.
    /// </summary>
    public static AkkaConfigurationBuilder WithDaemonSqlPersistence(
        this AkkaConfigurationBuilder builder, string connectionString)
    {
        // The breaker does not cancel the SQL command. A breaker that is shorter than the SQLite busy
        // wait fails the persist while the command still waits on a lock. A failed persist stops the
        // session actor. With a longer breaker, a lock wait only delays the write.
        var sqliteTimeout = TimeSpan.FromSeconds(new SqliteConnectionStringBuilder(connectionString).DefaultTimeout);
        var callTimeout = sqliteTimeout + CircuitBreakerMargin;
        return builder
            .WithSqlPersistence(connectionString: connectionString, providerName: "SQLite.MS")
            .AddHocon($$"""
                akka.persistence.journal.sql.circuit-breaker.call-timeout = {{(int)callTimeout.TotalSeconds}}s
                akka.persistence.snapshot-store.sql.circuit-breaker.call-timeout = {{(int)callTimeout.TotalSeconds}}s
                """, HoconAddMode.Prepend);
    }
}
