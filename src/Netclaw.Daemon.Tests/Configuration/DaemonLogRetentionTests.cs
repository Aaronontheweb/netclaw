// -----------------------------------------------------------------------
// <copyright file="DaemonLogRetentionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class DaemonLogRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-20T12:00:00Z");

    private readonly string _root = Path.Join(Path.GetTempPath(), $"netclaw-log-retention-tests-{Guid.NewGuid():N}");
    private string LogsDir => Path.Join(_root, "logs");

    public DaemonLogRetentionTests() => Directory.CreateDirectory(LogsDir);

    [Fact]
    public void Deletes_daemon_and_crash_logs_older_than_the_limit_and_keeps_the_rest()
    {
        // Cutoff is 2026-05-06 (today minus 14 days): that day stays, the day before goes.
        var old = new[] { "daemon-2026-05-05.log", "daemon-2026-01-01.log", "crash-20260505-235959.log", "crash-20260101-000000-4242-123-1.log", "crash-20260102-000000-4242-0123456789abcdef0123456789abcdef.log" };
        var kept = new[] { "daemon-2026-05-06.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log", "crash-20260506-000000.log", "crash-20260520-110000.log" };
        Touch(old);
        Touch(kept);

        var (deleted, failed) = DaemonLogRetention.Prune(LogsDir, Now, 14);

        Assert.Equal(old.Length, deleted);
        Assert.Equal(0, failed);
        Assert.All(old, f => Assert.False(File.Exists(Path.Join(LogsDir, f)), f));
        Assert.All(kept, f => Assert.True(File.Exists(Path.Join(LogsDir, f)), f));
    }

    [Fact]
    public void Leaves_unrelated_and_unparseable_files_and_subdirectories_alone()
    {
        var untouched = new[]
        {
            "daemon.log", "daemon-notadate.log", "daemon-2026-05-05.log.gz", "provider-probe.log", "headless-errors.log",
            "signalr-C1-T1.log", "crash-oops.log", "notes.txt", "daemon-2026-05-05.txt"
        };
        Touch(untouched);
        var sessionLog = Path.Join(LogsDir, "sessions", "C1-T1", "session.log");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionLog)!);
        File.WriteAllText(sessionLog, "x");
        File.SetLastWriteTimeUtc(sessionLog, Now.UtcDateTime.AddDays(-400));
        var outside = Path.Join(_root, "daemon-2020-01-01.log");
        File.WriteAllText(outside, "x");

        var (deleted, _) = DaemonLogRetention.Prune(LogsDir, Now, 14);

        Assert.Equal(0, deleted);
        Assert.All(untouched, f => Assert.True(File.Exists(Path.Join(LogsDir, f)), f));
        Assert.True(File.Exists(sessionLog));
        Assert.True(File.Exists(outside));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_or_negative_keeps_everything(int days)
    {
        Touch("daemon-2020-01-01.log", "crash-20200101-000000.log");

        var (deleted, failed) = DaemonLogRetention.Prune(LogsDir, Now, days);

        Assert.Equal((0, 0), (deleted, failed));
        Assert.Equal(2, Directory.GetFiles(LogsDir).Length);
    }

    [Fact]
    public void Missing_directory_is_a_no_op()
    {
        Assert.Equal((0, 0), DaemonLogRetention.Prune(Path.Join(_root, "nope"), Now, 14));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Undeletable_files_are_counted_not_thrown()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs POSIX permissions and a non-root user");
        Touch("daemon-2026-01-01.log", "daemon-2026-01-02.log", "daemon-2026-01-03.log", "daemon-2026-01-04.log", "daemon-2026-01-05.log");
        File.SetUnixFileMode(LogsDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (deleted, failed) = DaemonLogRetention.Prune(LogsDir, Now, 14);

            Assert.Equal(0, deleted);
            Assert.Equal(2, failed);
        }
        finally
        {
            File.SetUnixFileMode(LogsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Provider_prunes_at_first_write_and_keeps_the_active_file()
    {
        Touch("daemon-2026-04-01.log", "crash-20260401-120000.log", "daemon-2026-05-10.log", "daemon-2026-05-18.log", "daemon-2026-05-19.log", "provider-probe.log");
        var time = new FakeTimeProvider(Now);

        using (var provider = new RollingFileLoggerProvider(Path.Join(LogsDir, "daemon.log"), time, retentionDays: 14))
            provider.CreateLogger("Netclaw.Tests").LogInformation("hello");

        var names = Directory.GetFiles(LogsDir).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(["daemon-2026-05-10.log", "daemon-2026-05-18.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log", "provider-probe.log"], names);
        var active = File.ReadAllText(Path.Join(LogsDir, "daemon-2026-05-20.log"));
        Assert.Contains("hello", active, StringComparison.Ordinal);
        Assert.Contains("deleted 2 daemon/crash log file(s)", active, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_prunes_again_when_a_long_running_daemon_rolls_to_a_new_day()
    {
        var time = new FakeTimeProvider(Now);
        using (var provider = new RollingFileLoggerProvider(Path.Join(LogsDir, "daemon.log"), time, retentionDays: 14))
        {
            var logger = provider.CreateLogger("Netclaw.Tests");
            logger.LogInformation("day one");
            await WaitForTextAsync("daemon-2026-05-20.log", "day one");

            // Created while the daemon is already running, then the clock passes midnight. Three
            // newer daemon logs exist, so the old one is not protected by the keep-newest-3 rule.
            Touch("daemon-2026-05-06.log", "daemon-2026-05-17.log", "daemon-2026-05-18.log", "daemon-2026-05-19.log");
            time.Advance(TimeSpan.FromDays(1));
            logger.LogInformation("day two");
            await WaitForTextAsync("daemon-2026-05-21.log", "day two");
        }

        Assert.False(File.Exists(Path.Join(LogsDir, "daemon-2026-05-06.log")));
        Assert.True(File.Exists(Path.Join(LogsDir, "daemon-2026-05-20.log")));
        Assert.True(File.Exists(Path.Join(LogsDir, "daemon-2026-05-21.log")));
    }

    [Fact]
    public void Provider_with_retention_disabled_deletes_nothing()
    {
        Touch("daemon-2020-01-01.log", "daemon-2020-01-02.log", "daemon-2020-01-03.log", "daemon-2020-01-04.log");

        using (var provider = new RollingFileLoggerProvider(Path.Join(LogsDir, "daemon.log"), new FakeTimeProvider(Now), retentionDays: 0))
            provider.CreateLogger("Netclaw.Tests").LogInformation("hello");

        Assert.True(File.Exists(Path.Join(LogsDir, "daemon-2020-01-01.log")));
    }

    public static TheoryData<string> DecoyNames => new()
    {
        "daemon-2020-01-01-notes.log",
        "daemon-2020-01-01 my analysis.log",
        "daemon-2020-01-01.1.log",
        "daemon-2020-01-01.log.bak",
        "daemon-0001-01-01.log",
        "my-daemon-2020-01-01.log",
        "Daemon-2020-01-01.log",
        "crash-20200101-000000-userfile.log",
        "crash-20200101.log",
        "crash-20200101report.log",
        "crash-20200101-000000.log.bak",
        "Crash-20200101-000000.log",
        "crash-00010101-000000.log",
    };

    [Theory]
    [MemberData(nameof(DecoyNames))]
    public void Files_the_daemon_does_not_produce_are_never_deleted(string name)
    {
        // Four genuine newer daemon logs, so the keep-newest-3 rule is not what protects the decoy.
        Touch("daemon-2026-05-17.log", "daemon-2026-05-18.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log", name);

        var (deleted, failed) = DaemonLogRetention.Prune(LogsDir, Now, 14);

        Assert.Equal((0, 0), (deleted, failed));
        Assert.True(File.Exists(Path.Join(LogsDir, name)), name);
    }

    [Fact]
    public void A_matching_name_in_a_subdirectory_is_never_deleted()
    {
        Directory.CreateDirectory(Path.Join(LogsDir, "sessions"));
        Touch("daemon-2026-05-17.log", "daemon-2026-05-18.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log");
        Touch(Path.Join("sessions", "daemon-2020-01-01.log"), Path.Join("sessions", "crash-20200101-000000.log"));

        var (deleted, _) = DaemonLogRetention.Prune(LogsDir, Now, 14);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(Path.Join(LogsDir, "sessions", "daemon-2020-01-01.log")));
        Assert.True(File.Exists(Path.Join(LogsDir, "sessions", "crash-20200101-000000.log")));
    }

    [Fact]
    public void A_clock_far_in_the_future_still_leaves_the_newest_three_daemon_logs()
    {
        var recent = Enumerable.Range(11, 10).Select(d => $"daemon-2026-05-{d}.log").ToArray();
        Touch(recent);

        var (deleted, _) = DaemonLogRetention.Prune(LogsDir, DateTimeOffset.Parse("2099-01-01T00:00:00Z"), 14);

        Assert.Equal(7, deleted);
        Assert.Equal(["daemon-2026-05-18.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log"],
            Directory.GetFiles(LogsDir).Select(Path.GetFileName).Order().ToArray());
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(740_000)]
    public void A_retention_longer_than_the_calendar_keeps_everything_without_throwing(int days)
    {
        Touch("daemon-2020-01-01.log", "daemon-2020-01-02.log", "daemon-2020-01-03.log", "daemon-2020-01-04.log");

        Assert.Equal((0, 0), DaemonLogRetention.Prune(LogsDir, Now, days));
        Assert.Equal(4, Directory.GetFiles(LogsDir).Length);
    }

    [Fact]
    public void Provider_writes_its_first_line_even_with_an_enormous_retention()
    {
        using (var provider = new RollingFileLoggerProvider(Path.Join(LogsDir, "daemon.log"), new FakeTimeProvider(Now), retentionDays: int.MaxValue))
            provider.CreateLogger("Netclaw.Tests").LogInformation("first line");

        Assert.Contains("first line", File.ReadAllText(Path.Join(LogsDir, "daemon-2026-05-20.log")), StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void An_unreadable_logs_directory_is_counted_not_thrown()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs POSIX permissions and a non-root user");
        Touch("daemon-2020-01-01.log");
        File.SetUnixFileMode(LogsDir, UnixFileMode.None);
        try
        {
            Assert.Equal((0, 1), DaemonLogRetention.Prune(LogsDir, Now, 14));
        }
        finally
        {
            File.SetUnixFileMode(LogsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Retention_runs_once_per_utc_day_not_on_every_size_roll()
    {
        var time = new FakeTimeProvider(Now);
        var bigLine = new string('x', 1024 * 1024);
        using (var provider = new RollingFileLoggerProvider(Path.Join(LogsDir, "daemon.log"), time, retentionDays: 14))
        {
            var logger = provider.CreateLogger("Netclaw.Tests");
            // 12 MB crosses the 10 MB cap, so every later line forces a size roll of today's file.
            for (var i = 0; i < 12; i++)
                logger.LogInformation("{Line}", bigLine);
            logger.LogInformation("rolled once");
            await WaitForTextAsync("daemon-2026-05-20.log", "rolled once");

            Touch("daemon-2026-01-01.log", "daemon-2026-01-02.log", "daemon-2026-01-03.log", "daemon-2026-01-04.log");
            for (var i = 0; i < 3; i++)
                logger.LogInformation("{Line}", bigLine);
            logger.LogInformation("after the rolls");
            await WaitForTextAsync("daemon-2026-05-20.log", "after the rolls");
        }

        Assert.True(File.Exists(Path.Join(LogsDir, "daemon-2026-01-01.log")));
    }

    [Fact]
    public void Retention_days_are_read_from_Logging_File_RetentionDays()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:File:RetentionDays"] = "3"
        }).Build();

        Assert.Equal(3, DaemonLogRetention.ResolveRetentionDays(config, out var warning));
        Assert.Null(warning);
    }

    [Fact]
    public void A_missing_retention_key_uses_the_default()
    {
        var config = new ConfigurationBuilder().Build();

        Assert.Equal(14, DaemonLogRetention.ResolveRetentionDays(config, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public void A_non_integer_retention_falls_back_to_the_default_with_a_warning(string raw)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:File:RetentionDays"] = raw
        }).Build();

        Assert.Equal(14, DaemonLogRetention.ResolveRetentionDays(config, out var warning));
        Assert.Contains("Logging:File:RetentionDays", warning, StringComparison.Ordinal);
        Assert.Contains(raw, warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("abc", 14)]
    public void ConfigureNetclawLogging_applies_the_configured_retention_and_survives_a_bad_value(string configured, int expectedDaysKept)
    {
        var home = Path.Join(_root, "home");
        var paths = new Netclaw.Configuration.NetclawPaths(home);
        paths.EnsureDirectoriesExist();
        var today = DateTime.UtcNow.Date;
        var names = Enumerable.Range(1, 30).Select(n => $"daemon-{today.AddDays(-n):yyyy-MM-dd}.log").ToArray();
        foreach (var name in names)
            File.WriteAllText(Path.Join(paths.LogsDirectory, name), "x");

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:RetentionDays"] = configured });
        builder.ConfigureNetclawLogging(paths);
        using (var app = builder.Build())
        {
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Netclaw.Tests").LogInformation("first line");
            app.Services.GetRequiredService<RollingFileLoggerProvider>().Dispose();
        }

        var remaining = Directory.GetFiles(paths.LogsDirectory, "daemon-*.log").Select(Path.GetFileName).ToHashSet();
        // Kept: the N previous days (today-N is on the boundary and stays) plus the daemon's own file for today.
        Assert.Equal(expectedDaysKept + 1, remaining.Count);
        Assert.Contains(names[0], remaining);
        Assert.DoesNotContain(names[^1], remaining);
        var todayLog = Directory.GetFiles(paths.LogsDirectory, $"daemon-{today:yyyy-MM-dd}.log").Single();
        Assert.Contains("first line", File.ReadAllText(todayLog), StringComparison.Ordinal);
    }

    private void Touch(params string[] names)
    {
        foreach (var name in names)
            File.WriteAllText(Path.Join(LogsDir, name), "x");
    }

    // The writer thread is asynchronous; poll for the flushed line rather than sleeping a fixed time.
    // The file is opened with shared read/write because the writer thread still holds it.
    [SlopwatchSuppress("SW004", "The log writer is a background thread with no completion signal; the poll is bounded by a deadline.")]
    private async Task WaitForTextAsync(string fileName, string text)
    {
        var path = Path.Join(LogsDir, fileName);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if ((await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).Contains(text, StringComparison.Ordinal))
                    return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"{fileName} never contained '{text}'");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[DaemonLogRetentionTests] cleanup failed: {ex.Message}");
        }
    }
}
