// -----------------------------------------------------------------------
// <copyright file="DaemonPersistenceConfiguration.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Persistence.Sql.Hosting;
using LinqToDB;
using LinqToDB.Interceptors;
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Persistence;
using System.Data.Common;

namespace Netclaw.Daemon;

/// <summary>
/// Composes the daemon SQLite persistence for Akka. Program.cs and the tests call the same method,
/// so a test reads the configuration that the daemon uses.
/// </summary>
/// <remarks>
/// The circuit breaker does not cancel the SQL command. A lock wait shorter than the SQLite timeout only
/// delays the write. A longer lock wait fails the write with a SQLite busy error, which this class cannot
/// prevent. The breaker clock also counts the time that a write spends in the write queue.
/// </remarks>
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
        var timeoutSeconds = new SqliteConnectionStringBuilder(connectionString).DefaultTimeout;
        if (timeoutSeconds <= 0)
            throw new ArgumentException(
                "The SQLite connection string sets Default Timeout to " + timeoutSeconds + ". SQLite would wait forever on a lock, and no circuit-breaker timeout is longer than that. Set a positive Default Timeout.",
                nameof(connectionString));

        var sqliteTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        var callTimeout = sqliteTimeout + CircuitBreakerMargin;
        // Only DataOptions can carry the interceptor that sets synchronous=NORMAL on each plugin
        // connection. The options overload sets the same defaults as the connection-string overload
        // (identifier "sql", default plugin, auto-initialize). The HOCON provider-name must still name
        // SQLite.MS, because the plugin reads it from the HOCON and not from DataOptions.
        const string providerName = "SQLite.MS";
        var dataOptions = new DataOptions()
            .UseConnectionString(providerName, connectionString)
            .UseInterceptor(SynchronousNormalInterceptor.Instance);
        var journalOptions = new SqlJournalOptions
        {
            ConnectionString = connectionString,
            ProviderName = providerName,
            DataOptions = dataOptions
        };
        var snapshotOptions = new SqlSnapshotOptions
        {
            ConnectionString = connectionString,
            ProviderName = providerName,
            DataOptions = dataOptions
        };
        return builder
            .WithSqlPersistence(journalOptions, snapshotOptions)
            .AddHocon($$"""
                akka.persistence.journal.sql.circuit-breaker.call-timeout = {{(int)callTimeout.TotalSeconds}}s
                akka.persistence.snapshot-store.sql.circuit-breaker.call-timeout = {{(int)callTimeout.TotalSeconds}}s
                """, HoconAddMode.Prepend);
    }
}

/// <summary>
/// Runs <c>PRAGMA synchronous=NORMAL</c> on each connection that the Akka SQL plugin opens. See
/// <see cref="NetclawSqlite"/> for the reason. A failed pragma throws from the open call.
/// </summary>
internal sealed class SynchronousNormalInterceptor : ConnectionInterceptor
{
    public static readonly SynchronousNormalInterceptor Instance = new();

    private SynchronousNormalInterceptor() { }

    public override void ConnectionOpened(ConnectionEventData eventData, DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = NetclawSqlite.SynchronousNormalSql;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        ConnectionEventData eventData, DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = NetclawSqlite.SynchronousNormalSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
