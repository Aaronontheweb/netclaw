// -----------------------------------------------------------------------
// <copyright file="DaemonPersistenceOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>Persistence backends supported by the daemon.</summary>
public enum PersistenceProvider
{
    /// <summary>Persist actor state and durable metadata in SQLite.</summary>
    Sqlite,

    /// <summary>Keep actor state in memory while retaining required daemon metadata in SQLite.</summary>
    InMemory
}

/// <summary>Configures the daemon persistence backend and its backend-specific settings.</summary>
public sealed class DaemonPersistenceOptions
{
    /// <summary>Gets the selected persistence backend.</summary>
    public PersistenceProvider Provider { get; init; } = PersistenceProvider.Sqlite;

    /// <summary>Gets the SQLite settings used when <see cref="Provider"/> is <see cref="PersistenceProvider.Sqlite"/>.</summary>
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

/// <summary>Configures the SQLite persistence database and migration behavior.</summary>
public sealed class SqlitePersistenceOptions
{
    /// <summary>Gets the optional SQLite database path. The daemon default is used when omitted.</summary>
    public string? Path { get; init; }

    /// <summary>Gets whether the daemon applies SQLite migrations during startup.</summary>
    public bool AutoMigrate { get; init; } = true;
}
