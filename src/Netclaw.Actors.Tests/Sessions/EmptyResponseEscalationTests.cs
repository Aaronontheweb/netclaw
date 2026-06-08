// -----------------------------------------------------------------------
// <copyright file="EmptyResponseEscalationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Verifies the actor-level wiring of the per-turn empty/thinking-only response
/// bound (issue #1346): once <see cref="SessionConfig.MaxEmptyResponsesPerTurn"/>
/// is exceeded, the session makes one final LLM call with tools DISABLED
/// (<c>RetryWithoutTools</c> → <c>FireLlmCall(forceNoTools: true)</c>) and then
/// fails the turn instead of looping. The TurnStateTracker unit tests cover the
/// decision logic; this pins the actor's dispatch of that decision.
/// </summary>
public class EmptyResponseEscalationTests : LlmSessionTestBase
{
    private const int MaxEmptyResponses = 2;

    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();
    private readonly FakeToolAuditLogger _fakeAuditLogger = new();

    public EmptyResponseEscalationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            MaxEmptyResponsesPerTurn = MaxEmptyResponses,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);
        services.AddSingleton<IToolAuditLogger>(_fakeAuditLogger);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Repeated_thinking_only_responses_escalate_with_tools_disabled_then_fail_turn()
    {
        // The model never produces a reply — only reasoning. With the ceiling at 2,
        // the sequence is: Retry, Retry, RetryWithoutTools (tools off), Fail.
        for (var i = 0; i < 4; i++)
            _fakeChatClient.PlannedResponses.Enqueue(
                [new TextReasoningContent($"[fake thinking] still pondering #{i}...")]);

        var sessionId = new SessionId("test-channel/empty-escalation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();

        // Subscribe with None: lifecycle outputs (ErrorOutput, TurnCompleted) are
        // delivered regardless of filter, while thinking deltas are not — so the
        // assertions below are not interleaved with streaming noise.
        var subscriber = CreateTestProbe("empty-escalation-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.None
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Please answer"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.ProviderFailure, error.Category);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Four main-model calls: Retry, Retry, RetryWithoutTools, Fail.
        Assert.Equal(4, _fakeChatClient.CallCount);

        // The escalation call (the 4th, made via FireLlmCall(forceNoTools: true))
        // must have been issued with NO tools; the earlier calls had tools exposed.
        Assert.Empty(_fakeChatClient.ReceivedToolNames[3]);
        Assert.NotEmpty(_fakeChatClient.ReceivedToolNames[0]);

        // The model never emitted a tool call, so nothing executed.
        Assert.Equal(0, _fakeToolExecutor.CallCount);
    }
}
