// -----------------------------------------------------------------------
// <copyright file="SqliteTransactionHoldMonitorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Persistence;
using Xunit;

namespace Netclaw.Actors.Tests.Persistence;

/// <summary>
/// A Netclaw-owned SQLite write transaction that stays open holds the database write lock.
/// The journal then waits on that lock. The warning names the site that held it, so an
/// operator can find the holder from the log.
/// </summary>
public sealed class SqliteTransactionHoldMonitorTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLogger _logger = new();

    [Fact]
    public void Hold_longer_than_500_ms_logs_a_warning_with_the_site_and_the_duration()
    {
        var hold = SqliteTransactionHoldMonitor.Start(_logger, _time, "memory_store.upsert");

        _time.Advance(TimeSpan.FromMilliseconds(750));
        hold.Dispose();

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("sqlite_tx_held site=memory_store.upsert ms=750", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(500)]
    public void Hold_of_500_ms_or_less_logs_nothing(int heldMilliseconds)
    {
        var hold = SqliteTransactionHoldMonitor.Start(_logger, _time, "daily_stats.flush");

        _time.Advance(TimeSpan.FromMilliseconds(heldMilliseconds));
        hold.Dispose();

        Assert.Empty(_logger.Entries);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
