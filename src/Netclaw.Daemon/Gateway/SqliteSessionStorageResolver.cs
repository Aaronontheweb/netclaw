// -----------------------------------------------------------------------
// <copyright file="SqliteSessionStorageResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Resolves an immutable session storage binding with one immediate SQLite transaction.
/// </summary>
public sealed class SqliteSessionStorageResolver : ISessionStorageResolver
{
    private readonly string _connectionString;
    private readonly string _sessionsDirectory;
    private readonly string _sessionLogsDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SessionStoragePaths> _resolved =
        new(StringComparer.Ordinal);

    public SqliteSessionStorageResolver(NetclawPaths paths, TimeProvider timeProvider)
        : this(paths, timeProvider, paths.SessionsDirectory)
    {
    }

    public SqliteSessionStorageResolver(
        NetclawPaths paths,
        TimeProvider timeProvider,
        string sessionsDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsDirectory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        _sessionsDirectory = Path.GetFullPath(sessionsDirectory);
        _sessionLogsDirectory = paths.SessionLogsDirectory;
        _timeProvider = timeProvider;
    }

    public SessionStoragePaths Resolve(SessionId sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId.Value);

        return _resolved.GetOrAdd(sessionId.Value, _ => ResolveUncached(sessionId));
    }

    private SessionStoragePaths ResolveUncached(SessionId sessionId)
    {
        var sanitizedSessionId = SessionDirectoryHelper.SanitizeSessionId(sessionId);
        var legacySessionDirectory = SessionDirectoryHelper.GetSessionDirectory(
            sessionId,
            _sessionsDirectory);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        EnsureBindingsTable(connection, transaction);

        var binding = ReadBinding(connection, transaction, sessionId);
        if (binding is not null)
        {
            transaction.Commit();
            return SessionStoragePaths.CreateVersion2(binding.EnvelopeRoot);
        }

        if (HasLegacyData(
                connection,
                transaction,
                sessionId,
                legacySessionDirectory,
                sanitizedSessionId))
        {
            transaction.Commit();
            return SessionStoragePaths.CreateLegacy(
                legacySessionDirectory,
                _sessionLogsDirectory,
                sanitizedSessionId);
        }

        var envelopeRoot = new SessionStorageEnvelopeRoot(
            Path.Combine(_sessionsDirectory, CreateEnvelopeDirectoryName(sessionId, sanitizedSessionId)));
        var newBinding = new SessionStorageBinding(SessionStorageLayoutVersion.Version2, envelopeRoot);
        InsertBinding(connection, transaction, sessionId, newBinding);
        transaction.Commit();
        return SessionStoragePaths.CreateVersion2(envelopeRoot);
    }

    private static void EnsureBindingsTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS session_storage_bindings (
                session_id      TEXT NOT NULL PRIMARY KEY,
                layout_version  INTEGER NOT NULL,
                envelope_root   TEXT NOT NULL UNIQUE,
                created_at      INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS session_storage_bindings_envelope_root_idx
                ON session_storage_bindings(envelope_root);
            """;
        command.ExecuteNonQuery();
    }

    private static SessionStorageBinding? ReadBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT layout_version, envelope_root
            FROM session_storage_bindings
            WHERE session_id = $sessionId
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var version = new SessionStorageLayoutVersion(reader.GetInt32(0));
        if (version != SessionStorageLayoutVersion.Version2)
        {
            throw new NotSupportedException(
                $"Session '{sessionId.Value}' uses unsupported storage layout version {version.Value}.");
        }

        return new SessionStorageBinding(
            version,
            new SessionStorageEnvelopeRoot(reader.GetString(1)));
    }

    private bool HasLegacyData(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        string legacySessionDirectory,
        string sanitizedSessionId)
    {
        if (Directory.Exists(legacySessionDirectory)
            || Directory.Exists(Path.Combine(_sessionLogsDirectory, sanitizedSessionId)))
        {
            return true;
        }

        return HasCatalogEntry(connection, transaction, sessionId)
               || HasJournalEntry(connection, transaction, sessionId);
    }

    private static bool HasCatalogEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId)
    {
        if (!TableExists(connection, transaction, "sessions"))
            return false;

        var identityColumn = ColumnExists(connection, transaction, "sessions", "persistence_id")
            ? "persistence_id"
            : ColumnExists(connection, transaction, "sessions", "session_id")
                ? "session_id"
                : null;
        if (identityColumn is null)
            return false;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM sessions WHERE {identityColumn} = $identity)";
        command.Parameters.AddWithValue(
            "$identity",
            identityColumn == "persistence_id" ? $"session-{sessionId.Value}" : sessionId.Value);
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static bool HasJournalEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId)
    {
        if (!TableExists(connection, transaction, "journal"))
            return false;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM journal WHERE persistence_id = $persistenceId)";
        command.Parameters.AddWithValue("$persistenceId", $"session-{sessionId.Value}");
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name)";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void InsertBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        SessionStorageBinding binding)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO session_storage_bindings(
                session_id,
                layout_version,
                envelope_root,
                created_at)
            VALUES ($sessionId, $layoutVersion, $envelopeRoot, $createdAt)
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);
        command.Parameters.AddWithValue("$layoutVersion", binding.LayoutVersion.Value);
        command.Parameters.AddWithValue("$envelopeRoot", binding.EnvelopeRoot.Value);
        command.Parameters.AddWithValue(
            "$createdAt",
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static string CreateEnvelopeDirectoryName(SessionId sessionId, string sanitizedSessionId)
    {
        const int displayPrefixLength = 80;
        var displayPrefix = sanitizedSessionId.Length <= displayPrefixLength
            ? sanitizedSessionId
            : sanitizedSessionId[..displayPrefixLength];
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.Value));
        var suffix = Convert.ToHexStringLower(digest.AsSpan(0, 8));
        return $"{displayPrefix}-{suffix}";
    }
}
