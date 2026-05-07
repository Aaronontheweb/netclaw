// -----------------------------------------------------------------------
// <copyright file="SessionLogActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Routing;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionLogActorTests : TestKit
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    private static IActorRef SpawnDispatcher(
        ActorSystem sys,
        string basePath,
        TimeProvider timeProvider,
        TimeSpan? idleTimeout = null) =>
        sys.ActorOf(GenericChildPerEntityParent.CreateProps(
            new SessionMessageExtractor(),
            entityId => SessionLogActor.CreateProps(
                new SessionId(entityId),
                basePath,
                timeProvider,
                idleTimeout)));

    [Fact]
    public async Task ThinkingDeltaOutput_is_written_to_session_log()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"netclaw-session-log-tests-{Guid.NewGuid():N}");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T13:00:00Z"));
        var sessionId = new SessionId("channel/thread");

        try
        {
            var dispatcher = SpawnDispatcher(Sys, basePath, timeProvider);

            dispatcher.Tell(new ThinkingDeltaOutput
            {
                SessionId = sessionId,
                Delta = "step by step"
            }, ActorRefs.NoSender);

            await AwaitAssertAsync(async () =>
            {
                var logFile = SessionLogFile.GetLogPath(sessionId, basePath);
                var text = await File.ReadAllTextAsync(logFile, TestContext.Current.CancellationToken);
                Assert.Contains("Thinking delta: step by step", text, StringComparison.Ordinal);
            }, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task Recreated_session_log_actor_appends_to_same_canonical_file()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"netclaw-session-log-tests-{Guid.NewGuid():N}");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T13:10:00Z"));
        var sessionId = new SessionId("channel/thread");

        try
        {
            // Short idle timeout so the child evicts during the test without sleeping.
            // ReceiveTimeout is driven by the actor scheduler (real time), not
            // FakeTimeProvider; AwaitAssertAsync polls real time until it fires.
            var dispatcher = SpawnDispatcher(Sys, basePath, timeProvider, idleTimeout: TimeSpan.FromMilliseconds(150));

            dispatcher.Tell(new TextOutput { SessionId = sessionId, Text = "first" }, ActorRefs.NoSender);

            await AwaitAssertAsync(async () =>
            {
                var logFile = SessionLogFile.GetLogPath(sessionId, basePath);
                var text = await File.ReadAllTextAsync(logFile, TestContext.Current.CancellationToken);
                Assert.Contains("Assistant: first", text, StringComparison.Ordinal);
            }, cancellationToken: TestContext.Current.CancellationToken);

            dispatcher.Tell(new TextOutput { SessionId = sessionId, Text = "second" }, ActorRefs.NoSender);

            await AwaitAssertAsync(async () =>
            {
                var logFile = SessionLogFile.GetLogPath(sessionId, basePath);
                Assert.True(File.Exists(logFile));
                Assert.Single(Directory.GetFiles(Path.GetDirectoryName(logFile)!, "*.log", SearchOption.TopDirectoryOnly));

                var text = await File.ReadAllTextAsync(logFile, TestContext.Current.CancellationToken);
                Assert.Contains("Assistant: first", text, StringComparison.Ordinal);
                Assert.Contains("Assistant: second", text, StringComparison.Ordinal);
            }, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task Dispatcher_routes_messages_to_per_session_log_files()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"netclaw-session-log-tests-{Guid.NewGuid():N}");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T13:20:00Z"));
        var sessionA = new SessionId("ch/thread-a");
        var sessionB = new SessionId("ch/thread-b");

        try
        {
            var dispatcher = SpawnDispatcher(Sys, basePath, timeProvider);

            dispatcher.Tell(new TextOutput { SessionId = sessionA, Text = "alpha" }, ActorRefs.NoSender);
            dispatcher.Tell(new TextOutput { SessionId = sessionB, Text = "beta" }, ActorRefs.NoSender);

            await AwaitAssertAsync(async () =>
            {
                var pathA = SessionLogFile.GetLogPath(sessionA, basePath);
                var pathB = SessionLogFile.GetLogPath(sessionB, basePath);
                var textA = await File.ReadAllTextAsync(pathA, TestContext.Current.CancellationToken);
                var textB = await File.ReadAllTextAsync(pathB, TestContext.Current.CancellationToken);

                Assert.Contains("alpha", textA, StringComparison.Ordinal);
                Assert.DoesNotContain("beta", textA, StringComparison.Ordinal);
                Assert.Contains("beta", textB, StringComparison.Ordinal);
                Assert.DoesNotContain("alpha", textB, StringComparison.Ordinal);
            }, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task Dispatcher_writes_audit_and_diagnostic_to_same_session_log()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"netclaw-session-log-tests-{Guid.NewGuid():N}");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T13:30:00Z"));
        var sessionId = new SessionId("ch/thread");

        try
        {
            var dispatcher = SpawnDispatcher(Sys, basePath, timeProvider);

            dispatcher.Tell(new TextOutput { SessionId = sessionId, Text = "audit-line" }, ActorRefs.NoSender);
            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            dispatcher.Tell(new SessionLogDiagnostic
            {
                SessionId = sessionId,
                Line = "[2026-05-07T13:30:00.001+00:00] Diagnostic: provider sent request"
            }, ActorRefs.NoSender);

            await AwaitAssertAsync(async () =>
            {
                var path = SessionLogFile.GetLogPath(sessionId, basePath);
                var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                Assert.Contains("Assistant: audit-line", text, StringComparison.Ordinal);
                Assert.Contains("Diagnostic: provider sent request", text, StringComparison.Ordinal);
            }, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }
}
