// -----------------------------------------------------------------------
// <copyright file="SchemaMigrationHostedService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>Applies required SQLite schema migrations before dependent hosted services start.</summary>
public sealed class SchemaMigrationHostedService : IHostedService
{
    private readonly DaemonPersistenceOptions _options;
    private readonly NetclawPaths _paths;
    private readonly SchemaMigrator _migrator;
    private readonly SQLiteMemoryStore _memoryStore;
    private readonly ILogger<SchemaMigrationHostedService> _logger;

    /// <summary>Creates the startup migration service.</summary>
    /// <param name="options">The selected persistence settings.</param>
    /// <param name="paths">The daemon filesystem paths.</param>
    /// <param name="migrator">The SQLite schema migrator.</param>
    /// <param name="memoryStore">The independently persisted memory store.</param>
    /// <param name="logger">The startup logger.</param>
    public SchemaMigrationHostedService(
        DaemonPersistenceOptions options,
        NetclawPaths paths,
        SchemaMigrator migrator,
        SQLiteMemoryStore memoryStore,
        ILogger<SchemaMigrationHostedService> logger)
    {
        _options = options;
        _paths = paths;
        _migrator = migrator;
        _memoryStore = memoryStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Provider is not PersistenceProvider.Sqlite || _options.Sqlite.AutoMigrate)
        {
            var sqlitePath = _options.GetSqlitePath(_paths);
            _logger.LogInformation("Running SQLite schema migrations at {Path}", sqlitePath);
            await _migrator.MigrateAsync(sqlitePath, cancellationToken);
        }

        // Memory store always uses SQLite regardless of akka persistence provider,
        // so its schema must be initialized unconditionally.
        await _memoryStore.InitializeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
