// -----------------------------------------------------------------------
// <copyright file="SessionLogActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting.TestKit;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionLogActorTests : TestKit
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task ThinkingDeltaOutput_is_written_to_session_log()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"netclaw-session-log-tests-{Guid.NewGuid():N}");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T13:00:00Z"));
        var sessionId = new SessionId("channel/thread");

        try
        {
            var actor = Sys.ActorOf(SessionLogActor.CreateProps(sessionId, basePath, timeProvider));

            actor.Tell(new ThinkingDeltaOutput
            {
                SessionId = sessionId,
                Delta = "step by step"
            }, ActorRefs.NoSender);

            await AwaitAssertAsync(async () =>
            {
                var logFile = SessionLogActor.GetSessionLogPath(sessionId, basePath);
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
    public async Task Restarted_session_log_actor_appends_to_same_canonical_file()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"netclaw-session-log-tests-{Guid.NewGuid():N}");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T13:10:00Z"));
        var sessionId = new SessionId("channel/thread");

        try
        {
            var firstActor = Sys.ActorOf(SessionLogActor.CreateProps(sessionId, basePath, timeProvider));
            firstActor.Tell(new TextOutput
            {
                SessionId = sessionId,
                Text = "first"
            }, ActorRefs.NoSender);

            await AwaitAssertAsync(async () =>
            {
                var logFile = SessionLogActor.GetSessionLogPath(sessionId, basePath);
                var text = await File.ReadAllTextAsync(logFile, TestContext.Current.CancellationToken);
                Assert.Contains("Assistant: first", text, StringComparison.Ordinal);
            }, cancellationToken: TestContext.Current.CancellationToken);

            Watch(firstActor);
            Sys.Stop(firstActor);
            await ExpectTerminatedAsync(firstActor, cancellationToken: TestContext.Current.CancellationToken);

            var secondActor = Sys.ActorOf(SessionLogActor.CreateProps(sessionId, basePath, timeProvider));
            secondActor.Tell(new TextOutput
            {
                SessionId = sessionId,
                Text = "second"
            }, ActorRefs.NoSender);

            await AwaitAssertAsync(async () =>
            {
                var logFile = SessionLogActor.GetSessionLogPath(sessionId, basePath);
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
}
