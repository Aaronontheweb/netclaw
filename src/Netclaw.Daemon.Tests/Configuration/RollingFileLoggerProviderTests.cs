// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RollingFileLoggerProviderTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-rolling-logger-tests-{Guid.NewGuid():N}");

    [Fact]
    public void SessionScopedLogs_are_mirrored_into_canonical_session_log_file()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Combine(_basePath, "logs", "daemon.log");
        var sessionLogsPath = Path.Combine(_basePath, "logs", "sessions");
        var sessionId = new SessionId("channel/thread");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);

        SessionLogFile.AppendLine(sessionId, sessionLogsPath, "[2026-05-07T12:34:55.0000000+00:00] User: seeded line");

        using (var provider = new RollingFileLoggerProvider(daemonLogPath, sessionLogsPath, timeProvider))
        {
            var logger = provider.CreateLogger("Netclaw.Tests");

            using (SessionDiagnosticsContext.Push(sessionId.Value))
            {
                logger.LogInformation("session scoped message");
            }
        }

        var daemonLog = Directory.GetFiles(Path.Combine(_basePath, "logs"), "daemon-*.log", SearchOption.TopDirectoryOnly).Single();
        var daemonText = File.ReadAllText(daemonLog);
        Assert.Contains("session scoped message", daemonText, StringComparison.Ordinal);

        var sessionLog = SessionLogActor.GetSessionLogPath(sessionId, sessionLogsPath);
        var sessionText = File.ReadAllText(sessionLog);
        Assert.Contains("User: seeded line", sessionText, StringComparison.Ordinal);
        Assert.Contains("Diagnostic:", sessionText, StringComparison.Ordinal);
        Assert.Contains("session scoped message", sessionText, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(sessionLog)!, "*.log", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void DaemonLogs_without_session_context_do_not_create_session_log_file()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Combine(_basePath, "logs", "daemon.log");
        var sessionLogsPath = Path.Combine(_basePath, "logs", "sessions");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);

        using (var provider = new RollingFileLoggerProvider(daemonLogPath, sessionLogsPath, timeProvider))
        {
            var logger = provider.CreateLogger("Netclaw.Tests");
            logger.LogInformation("daemon message");
        }

        var daemonLog = Directory.GetFiles(Path.Combine(_basePath, "logs"), "daemon-*.log", SearchOption.TopDirectoryOnly).Single();
        var daemonText = File.ReadAllText(daemonLog);
        Assert.Contains("daemon message", daemonText, StringComparison.Ordinal);
        Assert.False(Directory.Exists(sessionLogsPath) && Directory.EnumerateFiles(sessionLogsPath, "*.log", SearchOption.AllDirectories).Any());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_basePath))
                Directory.Delete(_basePath, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[RollingFileLoggerProviderTests] cleanup failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"[RollingFileLoggerProviderTests] cleanup failed: {ex.Message}");
        }
    }
}
