// -----------------------------------------------------------------------
// <copyright file="DaemonPersistenceOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

public enum PersistenceProvider
{
    Sqlite,
    InMemory
}

public sealed class DaemonPersistenceOptions
{
    public PersistenceProvider Provider { get; init; } = PersistenceProvider.Sqlite;

    public SqlitePersistenceOptions Sqlite { get; init; } = new();

    /// <summary>
    /// Gets the SQLite database used by the selected persistence provider.
    /// In-memory persistence keeps durable Netclaw metadata in the default database.
    /// </summary>
    internal string GetSqlitePath(NetclawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Provider == PersistenceProvider.Sqlite && !string.IsNullOrWhiteSpace(Sqlite.Path)
            ? Sqlite.Path!
            : paths.SqliteDbPath;
    }
}

public sealed class SqlitePersistenceOptions
{
    public string? Path { get; init; }

    public bool AutoMigrate { get; init; } = true;
}
