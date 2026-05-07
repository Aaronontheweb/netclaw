// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RollingFileLoggerProviderTests : TestKit, IDisposable
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-rolling-logger-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Session_scoped_log_routes_diagnostic_to_dispatcher()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Combine(_basePath, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);
        var probe = CreateTestProbe();

        using (var provider = new RollingFileLoggerProvider(daemonLogPath, timeProvider))
        {
            provider.AttachSessionDispatcher(Task.FromResult<IActorRef>(probe.Ref));
            var logger = provider.CreateLogger("Netclaw.Tests");

            using (SessionDiagnosticsContext.Push("channel/thread"))
            {
                logger.LogInformation("session scoped message");
            }

            var diagnostic = await probe.ExpectMsgAsync<SessionLogDiagnostic>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("channel/thread", diagnostic.SessionId.Value);
            Assert.Contains("session scoped message", diagnostic.Line, StringComparison.Ordinal);
            Assert.Contains("Diagnostic:", diagnostic.Line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Daemon_scoped_log_does_not_route_to_dispatcher()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Combine(_basePath, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);
        var probe = CreateTestProbe();

        using (var provider = new RollingFileLoggerProvider(daemonLogPath, timeProvider))
        {
            provider.AttachSessionDispatcher(Task.FromResult<IActorRef>(probe.Ref));
            var logger = provider.CreateLogger("Netclaw.Tests");
            logger.LogInformation("daemon message");

            await probe.ExpectNoMsgAsync(
                TimeSpan.FromMilliseconds(200),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var daemonLog = Directory.GetFiles(Path.Combine(_basePath, "logs"), "daemon-*.log", SearchOption.TopDirectoryOnly).Single();
        var daemonText = await File.ReadAllTextAsync(daemonLog, TestContext.Current.CancellationToken);
        Assert.Contains("daemon message", daemonText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pre_resolution_diagnostics_buffer_and_drain_to_dispatcher()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Combine(_basePath, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);
        var probe = CreateTestProbe();
        var tcs = new TaskCompletionSource<IActorRef>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (var provider = new RollingFileLoggerProvider(daemonLogPath, timeProvider))
        {
            provider.AttachSessionDispatcher(tcs.Task);
            var logger = provider.CreateLogger("Netclaw.Tests");

            using (SessionDiagnosticsContext.Push("channel/thread"))
            {
                logger.LogInformation("pre-resolution one");
                logger.LogInformation("pre-resolution two");
            }

            await probe.ExpectNoMsgAsync(
                TimeSpan.FromMilliseconds(100),
                cancellationToken: TestContext.Current.CancellationToken);

            tcs.SetResult(probe.Ref);

            var first = await probe.ExpectMsgAsync<SessionLogDiagnostic>(
                cancellationToken: TestContext.Current.CancellationToken);
            var second = await probe.ExpectMsgAsync<SessionLogDiagnostic>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("pre-resolution one", first.Line, StringComparison.Ordinal);
            Assert.Contains("pre-resolution two", second.Line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Diagnostic_routes_correctly_when_logged_from_async_continuation()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Combine(_basePath, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);
        var probe = CreateTestProbe();

        using (var provider = new RollingFileLoggerProvider(daemonLogPath, timeProvider))
        {
            provider.AttachSessionDispatcher(Task.FromResult<IActorRef>(probe.Ref));
            var logger = provider.CreateLogger("Netclaw.Tests");

            using (SessionDiagnosticsContext.Push("channel/thread"))
            {
                await Task.Yield();
                logger.LogInformation("post-await");
            }

            var diagnostic = await probe.ExpectMsgAsync<SessionLogDiagnostic>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("channel/thread", diagnostic.SessionId.Value);
            Assert.Contains("post-await", diagnostic.Line, StringComparison.Ordinal);
        }
    }

    void IDisposable.Dispose()
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
