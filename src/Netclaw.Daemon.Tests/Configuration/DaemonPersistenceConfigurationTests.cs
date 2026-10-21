// -----------------------------------------------------------------------
// <copyright file="DaemonPersistenceConfigurationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Microsoft.Data.Sqlite;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Production incident, 21 Oct 2026: a SQLite write lock held by another connection made a journal
/// write wait longer than the 10 s circuit-breaker call timeout. The breaker failed the persist,
/// and the session actor stopped. Microsoft.Data.Sqlite waits on a busy lock until its command
/// timeout (30 s by default). The journal and snapshot breakers must wait longer than that.
/// </summary>
public sealed class DaemonPersistenceConfigurationTests : Akka.Hosting.TestKit.TestKit, IAsyncDisposable
{
    private const string JournalPluginId = "akka.persistence.journal.sql";
    private const string SnapshotPluginId = "akka.persistence.snapshot-store.sql";
    private const string JournalFallbackId = "akka.persistence.journal-plugin-fallback";
    private const string SnapshotFallbackId = "akka.persistence.snapshot-store-plugin-fallback";

    private readonly DisposableTempDir _directory = new();

    public DaemonPersistenceConfigurationTests(ITestOutputHelper output) : base(output: output) { }

    private string ConnectionString => $"Data Source={Path.Combine(_directory.Path, "netclaw.db")}";

    // The test host composes persistence through the same method that Program.cs calls.
    // Thus a change to that method changes the effective configuration that the tests read.
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.WithDaemonSqlPersistence(ConnectionString);

    [Fact]
    public void Journal_circuit_breaker_waits_longer_than_the_sqlite_busy_wait()
    {
        var busyWait = SqliteBusyWait(ConnectionString);

        var callTimeout = EffectiveCallTimeout(JournalPluginId, JournalFallbackId);

        Assert.True(
            callTimeout > busyWait,
            $"The journal circuit-breaker call-timeout is {callTimeout}. SQLite waits {busyWait} on a busy lock. " +
            "A lock wait must not fail the persist.");
    }

    [Fact]
    public void Snapshot_store_circuit_breaker_waits_longer_than_the_sqlite_busy_wait()
    {
        var busyWait = SqliteBusyWait(ConnectionString);

        var callTimeout = EffectiveCallTimeout(SnapshotPluginId, SnapshotFallbackId);

        Assert.True(
            callTimeout > busyWait,
            $"The snapshot-store circuit-breaker call-timeout is {callTimeout}. SQLite waits {busyWait} on a busy lock. " +
            "A lock wait must not fail the snapshot save.");
    }

    // Akka builds a plugin config as the plugin section with the plugin-fallback section behind it.
    // The SQL plugin sets no call-timeout of its own, so the fallback value (10 s for the journal,
    // 20 s for the snapshot store) applies unless the daemon configuration overrides it.
    private TimeSpan EffectiveCallTimeout(string pluginId, string fallbackId)
    {
        _ = Akka.Persistence.Persistence.Instance.Apply(Sys);
        var config = Sys.Settings.Config;
        return config.GetConfig(pluginId)
            .WithFallback(config.GetConfig(fallbackId))
            .GetTimeSpan("circuit-breaker.call-timeout");
    }

    // The wait that Microsoft.Data.Sqlite applies to a command on this exact connection string.
    // It reads the "Default Timeout" key and falls back to 30 s when the key is absent.
    private static TimeSpan SqliteBusyWait(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        return TimeSpan.FromSeconds(connection.DefaultTimeout);
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _directory.Dispose();
        }
    }
}
