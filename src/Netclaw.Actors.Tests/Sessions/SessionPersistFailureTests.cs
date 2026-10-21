// -----------------------------------------------------------------------
// <copyright file="SessionPersistFailureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Hosting;
using Akka.Persistence.Journal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Production incident, 21 Oct 2026: the journal write of a <see cref="ToolBatchStarted"/> event
/// exceeded the circuit-breaker call timeout. The session actor stopped and sent nothing to the
/// Slack thread. A persist failure of a non-input event must tell the channel before the stop.
/// </summary>
public sealed class SessionToolBatchPersistFailureTests(ITestOutputHelper output)
    : SessionPersistFailureTestBase<ToolBatchStartedFailingJournal>(output)
{
    [Fact]
    public async Task Failed_tool_batch_write_tells_the_channel_before_the_session_stops()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        ScriptSearchToolCallOnFirstModelCall();
        var sessionId = new SessionId("persist-failure/tool-batch");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var channel = CreateTestProbe("persist-failure-channel");
        await JoinSessionAsync(manager, channel, sessionId, ChannelFilter);
        var session = await ResolveSessionAsync(sessionId);
        var watcher = CreateTestProbe("persist-failure-watcher");
        watcher.Watch(session);

        // Act
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a tool"
        }, TimeSpan.FromSeconds(5), ct);

        // Assert
        var error = await channel.FishForMessageAsync<ErrorOutput>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: ct);
        Assert.Contains("save", error.Message, StringComparison.OrdinalIgnoreCase);
        await watcher.ExpectTerminatedAsync(session, TimeSpan.FromSeconds(10), cancellationToken: ct);
    }
}

/// <summary>
/// Guards the existing contract for an input event. The caller of an admission gets a Nack, and
/// the session starts no model call.
/// </summary>
public sealed class SessionInputPersistFailureTests(ITestOutputHelper output)
    : SessionPersistFailureTestBase<InputAdmittedFailingJournal>(output)
{
    [Fact]
    public async Task Failed_input_admission_write_nacks_the_sender_and_starts_no_model_call()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var sessionId = new SessionId("persist-failure/input");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var channel = CreateTestProbe("persist-failure-input-channel");
        await JoinSessionAsync(manager, channel, sessionId, ChannelFilter);

        // Act
        var reply = await manager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello"
        }, TimeSpan.FromSeconds(10), ct);

        // Assert
        var nack = Assert.IsType<CommandNack>(reply);
        Assert.Contains("journal", nack.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ModelCallCount);
    }
}

public abstract class SessionPersistFailureTestBase<TJournal>(ITestOutputHelper output) : LlmSessionTestBase(output)
    where TJournal : MemoryJournal
{
    protected const OutputFilter ChannelFilter = OutputFilter.Text | OutputFilter.Files | OutputFilter.ProcessingState;

    private readonly FakeChatClient _client = new();

    protected int ModelCallCount => _client.CallCount;

    protected void ScriptSearchToolCallOnFirstModelCall()
        => _client.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "search_tools", new Dictionary<string, object?>
            {
                ["_rationale"] = "Read the current tool list."
            })
        ];

    protected override void ConfigureSessionPersistence(AkkaConfigurationBuilder builder)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore();
        builder.AddHocon($"""
            akka.persistence.journal.inmem.class = "{typeof(TJournal).AssemblyQualifiedName}"
            """, HoconAddMode.Prepend);
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { SnapshotInterval = 0, TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("Test assistant."));
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "same result", "search_tools"), "builtin");
        services.AddSingleton(registry);
    }

    protected async Task<IActorRef> ResolveSessionAsync(SessionId sessionId)
        => await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
}

/// <summary>
/// A journal that fails the write of one event type. It throws a <see cref="TimeoutException"/>,
/// as the circuit breaker does when a write waits on a SQLite lock.
/// </summary>
public abstract class FailingEventJournal : MemoryJournal
{
    protected abstract Type FailingEventType { get; }

    protected override async Task<IImmutableList<Exception>> WriteMessagesAsync(
        IEnumerable<AtomicWrite> messages, CancellationToken cancellationToken)
    {
        var writes = messages.ToArray();
        var payloads = writes
            .SelectMany(write => (IEnumerable<IPersistentRepresentation>)write.Payload)
            .Select(item => item.Payload);
        if (payloads.Any(FailingEventType.IsInstanceOfType))
            throw new TimeoutException($"The test journal failed the write of {FailingEventType.Name}.");

        return await base.WriteMessagesAsync(writes, cancellationToken);
    }
}

public sealed class ToolBatchStartedFailingJournal : FailingEventJournal
{
    protected override Type FailingEventType => typeof(ToolBatchStarted);
}

public sealed class InputAdmittedFailingJournal : FailingEventJournal
{
    protected override Type FailingEventType => typeof(InputAdmitted);
}
