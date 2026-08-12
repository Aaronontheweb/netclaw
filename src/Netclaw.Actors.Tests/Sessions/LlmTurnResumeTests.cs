// -----------------------------------------------------------------------
// <copyright file="LlmTurnResumeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Covers bounded turn-level resume after a mid-stream LLM call timeout (see
/// <c>LlmSessionActor.TryResumeAfterTimeout</c>). Evidence: correlated provider
/// stall storms (a few tokens then silence) previously burned the full watchdog
/// budget and then failed the turn terminally — in headless <c>chat -p</c> mode a
/// failed turn is a failed session with no external retry. These tests prove the
/// discard-and-resume mechanism, its retry budget, the structural (not
/// tool-iteration-gated) safety of resuming any call in the turn, and that a
/// resumed call's <see cref="TextDeltaOutput"/> stream never corrupts a
/// delta-accumulating consumer's final answer — using
/// <see cref="LlmSessionTestBase.UseTestScheduler"/> so the watchdog fires only on
/// an explicit <see cref="LlmSessionTestBase.AdvanceScheduler"/> — no wall-clock race.
/// </summary>
public sealed class LlmTurnResumeTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private static readonly TimeSpan FirstTokenTimeout = TimeSpan.FromSeconds(2);
    private readonly ResumeTestChatClient _chatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();

    protected override bool UseTestScheduler => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "turn-resume-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            PrefillTimeout = FirstTokenTimeout,
            FirstTokenTimeout = FirstTokenTimeout,
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            SidecarLlmTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
                TimeoutResumeRetryBudget = 2,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Timeout_with_no_tool_call_discards_partial_content_and_resumes_successfully()
    {
        const string partialMarker = "STALLED_PARTIAL_MARKER_SHOULD_NOT_APPEAR";
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("stalled chunk one ", partialMarker));
        // The resumed call also streams multiple real deltas (not a single-shot
        // completion) so the C1 proof below exercises the same delta-accumulation
        // path the dead call used, not just the TextOutput fallback.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.MultiDeltaTextThenComplete("Resumed answer ", "after timeout"));

        var sessionId = new SessionId("turn-resume/success");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-success-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // First call: wait for genuine partial streaming (proves a real stall, not
        // an instant failure), then let the watchdog fire.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);

        // Collect EVERY output the turn emits, in order, through TurnCompleted — the
        // exact event stream a delta-accumulating subscriber (headless JSON
        // envelope, webhook/reminder ExecutionOutputAccumulator, chat TUI) sees.
        var events = new List<object>();
        var advanced = false;
        object msg;
        do
        {
            msg = await subscriber.ExpectMsgAsync<object>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
            events.Add(msg);

            if (!advanced && msg is TextDeltaOutput d && d.Delta.Contains(partialMarker, StringComparison.Ordinal))
            {
                advanced = true;
                AdvanceScheduler(FirstTokenTimeout);
                await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
            }
        } while (msg is not TurnCompleted);

        var completed = Assert.IsType<TurnCompleted>(msg);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        // C1 proof: feed the exact production event sequence into the real
        // ExecutionOutputAccumulator (shared by ReminderExecutionActor and
        // WebhookExecutionActor). Before the TextStreamDiscarded fix this would
        // accumulate "stalled chunk one STALLED_PARTIAL_MARKER_SHOULD_NOT_APPEARResumed
        // answer after timeout" — the dead call's partial text glued to the
        // resumed call's answer.
        var accumulator = new ExecutionOutputAccumulator(new ToolName("notify_channel"));
        foreach (var evt in events.OfType<SessionOutput>())
            accumulator.ProcessOutput(evt);

        Assert.Equal("Resumed answer after timeout", accumulator.GetAccumulatedText());
        Assert.DoesNotContain(partialMarker, accumulator.GetAccumulatedText(), StringComparison.Ordinal);

        // The discard signal must land strictly between the dead call's last delta
        // and the resumed call's first delta — proving the actor clears
        // subscriber buffers before the resumed stream starts, not after.
        var discardIndex = events.FindIndex(e => e is TextStreamDiscarded);
        var deadMarkerIndex = events.FindIndex(e => e is TextDeltaOutput dd && dd.Delta.Contains(partialMarker, StringComparison.Ordinal));
        var resumedDeltaIndex = events.FindIndex(e => e is TextDeltaOutput rd && rd.Delta.Contains("Resumed answer", StringComparison.Ordinal));
        Assert.True(discardIndex > 0, "Expected a TextStreamDiscarded output for the resumed turn.");
        Assert.True(discardIndex > deadMarkerIndex, "Discard signal must arrive after the dead call's partial content.");
        Assert.True(resumedDeltaIndex > discardIndex, "Resumed call's deltas must arrive after the discard signal.");

        // The final TextOutput (independent of delta accumulation) must also be clean.
        var finalText = Assert.IsType<TextOutput>(events.OfType<TextOutput>().Single());
        Assert.Equal("Resumed answer after timeout", finalText.Text);
        Assert.DoesNotContain(partialMarker, finalText.Text, StringComparison.Ordinal);

        Assert.Equal(2, _chatClient.CallCount);

        // The resumed call re-issued the SAME messages as the dead call: identical
        // role/text sequence, proving no mutation and no extra user message.
        AssertIdenticalMessageLists(_chatClient.ReceivedMessages[0], _chatClient.ReceivedMessages[1]);

        // Persistence check: the discarded partial content must never have entered
        // _state.History — prove it by sending a follow-up turn and confirming the
        // marker never resurfaces in the conversation history sent to the LLM.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.InstantText("third response"));
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second message"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextOutput t && t.Text == "third response",
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);

        var thirdCallMessages = _chatClient.ReceivedMessages[2];
        Assert.DoesNotContain(thirdCallMessages, m => m.Text?.Contains(partialMarker, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Timeout_resume_budget_exhausted_fails_turn_exactly_as_before()
    {
        // Budget is 2 (configured in ConfigureSessionServices): the initial call
        // plus 2 resumes must all stall before the turn fails.
        for (var i = 0; i < 3; i++)
            _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas($"stall {i} chunk one ", $"stall {i} chunk two"));

        var sessionId = new SessionId("turn-resume/budget-exhausted");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-budget-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
            await subscriber.FishForMessageAsync<object>(
                m => m is TextDeltaOutput d && d.Delta.Contains($"stall {attempt} chunk two", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
            AdvanceScheduler(FirstTokenTimeout);
        }

        var error = await subscriber.FishForMessageAsync<object>(
            m => m is ErrorOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        var errorOutput = Assert.IsType<ErrorOutput>(error);
        Assert.Equal(ErrorCategory.Timeout, errorOutput.Category);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);

        // Exactly 3 calls: the original plus the 2-call resume budget. No fourth
        // (unbounded) resume attempt.
        Assert.Equal(3, _chatClient.CallCount);
    }

    [Fact]
    public async Task Timeout_during_restart_drain_fails_turn_without_resuming()
    {
        // Stall with multiple real deltas so the watchdog fire is a genuine stall,
        // not an instant failure.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("chunk one ", "chunk two"));

        var sessionId = new SessionId("turn-resume/restart-drain");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-restart-drain-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains("chunk two", StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);

        // Request a coordinated daemon restart drain WHILE the call is in flight.
        // TryResumeAfterTimeout must refuse once this lands, even though the retry
        // budget (2) is not exhausted — resuming would keep the turn alive and
        // block the drain.
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);
        var drainTask = sessionManager.Ask<CommandAck>(
            new PrepareForDaemonRestart(sessionId, "config-reload"),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // PrepareForDaemonRestart's own ack is deferred until the drain completes
        // (which only happens once this turn resolves), so it cannot be awaited
        // here without deadlocking. Instead, round-trip a second ask through the
        // same (sessionManager -> child) path and wait for ITS reply: sessionManager
        // forwards synchronously, so this reply cannot land before the drain
        // request was already dequeued and processed by the child, guaranteeing
        // _restartDrainRequested is set before the scheduler is advanced below. A
        // same-filter rejoin is a no-op that only acks the caller — it does not
        // re-emit SessionJoined to the subscriber, so nothing else to await here.
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        AdvanceScheduler(FirstTokenTimeout);

        var error = await subscriber.FishForMessageAsync<object>(
            m => m is ErrorOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.Timeout, Assert.IsType<ErrorOutput>(error).Category);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);

        // No resume attempt happened — exactly the one dead call.
        Assert.Equal(1, _chatClient.CallCount);

        // The drain completes and the session actor passivates, proving the failed
        // turn (not a resume) let the coordinated restart proceed. No observer is
        // configured in this test fixture, so passivation skips straight to its
        // short PassivationFinalStopDelay (100ms) grace window. The timer is
        // registered asynchronously on the actor's own dispatcher (after this
        // test's earlier AdvanceScheduler call already returned), so poll — each
        // retry nudges the virtual clock a little further until the actor has
        // caught up and the grace window timer fires.
        await AwaitAssertAsync(() =>
        {
            AdvanceScheduler(TimeSpan.FromMilliseconds(50));
            Assert.True(drainTask.IsCompleted, "Expected the restart drain to complete once the failed turn released passivation.");
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(sessionId, (await drainTask).SessionId);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Timeout_after_tool_call_dispatched_resumes_successfully()
    {
        // First call dispatches a tool call and completes normally.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.InstantToolCall(
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test query" })));
        // Second call — the post-tool follow-up — stalls with multiple real deltas,
        // then times out. This is the dominant real-world failure: C2 found the
        // pre-fix ToolIterationCount gate refused resume for every one of the
        // motivating stall reports because they all happened after at least one
        // completed tool iteration. Safety here is structural, not gate-based: tool
        // dispatch only happens in HandleLlmResponseReceived on a fully completed
        // response, and this call times out mid-stream, so it can never have
        // dispatched a tool call itself.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("post-tool chunk one ", "post-tool chunk two"));
        // Third call — the resume — completes normally.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.MultiDeltaTextThenComplete("Resumed ", "after tool call"));

        var sessionId = new SessionId("turn-resume/tool-gate");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-gate-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Drain the tool call/result from the first (successful) call.
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Second call (post-tool) stalls; let the watchdog fire.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains("post-tool chunk two", StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        AdvanceScheduler(FirstTokenTimeout);

        // Third call (the resume) completes cleanly — no ErrorOutput/TurnCompleted
        // in between, proving the turn resumed instead of failing.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        var text = await subscriber.FishForMessageAsync<object>(
            m => m is TextOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        var finalText = Assert.IsType<TextOutput>(text);
        Assert.Equal("Resumed after tool call", finalText.Text);
        Assert.DoesNotContain("post-tool chunk", finalText.Text, StringComparison.Ordinal);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        // Exactly 3 calls: the tool-call round, the stalled follow-up, and the
        // resumed follow-up. The tool executor ran exactly once — resume never
        // dispatches a tool call, so there is no double execution.
        Assert.Equal(3, _chatClient.CallCount);
        Assert.Equal(1, _fakeToolExecutor.CallCount);

        // The resumed call (index 2) re-issued the SAME messages as the dead call
        // (index 1) — including the tool-call/tool-result content from the earlier
        // completed iteration — and exposed the same tools.
        AssertIdenticalMessageLists(_chatClient.ReceivedMessages[1], _chatClient.ReceivedMessages[2]);
        AssertIdenticalTools(_chatClient.ReceivedOptions[1], _chatClient.ReceivedOptions[2]);
    }

    /// <summary>
    /// Asserts two message lists are identical in role, text, and tool-call /
    /// tool-result content — used to prove a resumed call re-sends the exact same
    /// prompt as the call it replaced, including turns with tool activity.
    /// </summary>
    private static void AssertIdenticalMessageLists(
        IReadOnlyList<ChatMessage> expected, IReadOnlyList<ChatMessage> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Role, actual[i].Role);
            Assert.Equal(expected[i].Text, actual[i].Text);

            var expectedCalls = expected[i].Contents.OfType<FunctionCallContent>().ToList();
            var actualCalls = actual[i].Contents.OfType<FunctionCallContent>().ToList();
            Assert.Equal(expectedCalls.Count, actualCalls.Count);
            for (var c = 0; c < expectedCalls.Count; c++)
            {
                Assert.Equal(expectedCalls[c].CallId, actualCalls[c].CallId);
                Assert.Equal(expectedCalls[c].Name, actualCalls[c].Name);
            }

            var expectedResults = expected[i].Contents.OfType<FunctionResultContent>().ToList();
            var actualResults = actual[i].Contents.OfType<FunctionResultContent>().ToList();
            Assert.Equal(expectedResults.Count, actualResults.Count);
            for (var r = 0; r < expectedResults.Count; r++)
            {
                Assert.Equal(expectedResults[r].CallId, actualResults[r].CallId);
                Assert.Equal(expectedResults[r].Result?.ToString(), actualResults[r].Result?.ToString());
            }
        }
    }

    /// <summary>
    /// Asserts two <see cref="ChatOptions"/> exposed the same tool names —
    /// proving a resumed call offered the LLM the same tool surface as the call
    /// it replaced.
    /// </summary>
    private static void AssertIdenticalTools(ChatOptions? expected, ChatOptions? actual)
    {
        var expectedNames = (expected?.Tools ?? []).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var actualNames = (actual?.Tools ?? []).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedNames, actualNames);
    }
}

/// <summary>
/// Separate fixture from <see cref="LlmTurnResumeTests"/> because it needs
/// <see cref="SessionConfig.PrefillTimeout"/> and <see cref="SessionConfig.FirstTokenTimeout"/>
/// to differ by an order of magnitude — <see cref="LlmTurnResumeTests"/> deliberately
/// sets them equal so its tests do not depend on the watchdog's arm timeout. Covers
/// H3: a resumed call must be armed on the promoted (tighter) budget, not the full
/// prefill budget, so the retry budget cannot triple the time to a final failure.
/// </summary>
public sealed class LlmTurnResumeWatchdogArmingTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private static readonly TimeSpan FirstTokenTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrefillTimeout = TimeSpan.FromMinutes(30);
    private readonly ResumeTestChatClient _chatClient = new();

    protected override bool UseTestScheduler => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "turn-resume-arming-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            PrefillTimeout = PrefillTimeout,
            FirstTokenTimeout = FirstTokenTimeout,
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            SidecarLlmTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
                // A budget of 1 keeps this a clean two-call scenario: the dead call,
                // then the resume whose own expiry exhausts the budget.
                TimeoutResumeRetryBudget = 1,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        services.AddSingleton(new ToolRegistry());
    }

    [Fact]
    public async Task Resumed_call_is_armed_on_promoted_budget_not_full_prefill_budget()
    {
        // First call: stall after two real deltas so its own watchdog promotes to
        // FirstTokenTimeout before firing — a genuine stall, not an instant failure.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("chunk one ", "chunk two"));
        // Resumed call: stall from the very first update, with zero deltas ever
        // streamed. This isolates the INITIAL watchdog arm timeout (what H3 fixes)
        // from the two-phase promotion, which only kicks in once a delta arrives.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallImmediately());

        var sessionId = new SessionId("turn-resume/watchdog-arming");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-arming-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains("chunk two", StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        AdvanceScheduler(FirstTokenTimeout);

        // The resume fires with zero deltas ever streamed. If it were armed on the
        // full 30-minute prefill budget (the H3 bug), advancing only
        // FirstTokenTimeout here would never fire its watchdog, and the bounded
        // timeout inside WaitForStreamInvocationAsync (M4) would turn the resulting
        // hang into a clear TimeoutException instead of blocking this test forever.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        AdvanceScheduler(FirstTokenTimeout);

        // Budget is 1: the resume's own watchdog expiry exhausts it, so the turn
        // fails — proving the resumed call's watchdog fired at FirstTokenTimeout,
        // not the 30-minute PrefillTimeout.
        var error = await subscriber.FishForMessageAsync<object>(
            m => m is ErrorOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.Timeout, Assert.IsType<ErrorOutput>(error).Category);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);
        Assert.Equal(2, _chatClient.CallCount);
    }
}

/// <summary>
/// One configured behavior for a single <see cref="ResumeTestChatClient"/> call,
/// dequeued in call order.
/// </summary>
internal enum ResumeCallBehaviorKind { StallAfterDeltas, StallImmediately, InstantText, MultiDeltaTextThenComplete, InstantToolCall }

internal sealed record ResumeCallBehavior(
    ResumeCallBehaviorKind Kind,
    string? Delta1 = null,
    string? Delta2 = null,
    string? Text = null,
    FunctionCallContent? ToolCall = null)
{
    /// <summary>
    /// Streams two substantive text deltas (needed so the session's
    /// buffered-first-delta trick actually flushes visible content — a single
    /// delta stays buffered pending a second) then hangs forever, simulating a
    /// half-open provider stream: a few tokens, then silence.
    /// </summary>
    public static ResumeCallBehavior StallAfterDeltas(string delta1, string delta2)
        => new(ResumeCallBehaviorKind.StallAfterDeltas, Delta1: delta1, Delta2: delta2);

    /// <summary>
    /// Hangs forever without ever streaming a single update — not even a
    /// keepalive. Isolates the watchdog's initial arm timeout from its
    /// stream-progress promotion logic.
    /// </summary>
    public static ResumeCallBehavior StallImmediately()
        => new(ResumeCallBehaviorKind.StallImmediately);

    public static ResumeCallBehavior InstantText(string text)
        => new(ResumeCallBehaviorKind.InstantText, Text: text);

    /// <summary>
    /// Streams two substantive text deltas (same buffered-first-delta
    /// requirement as <see cref="StallAfterDeltas"/>) and then completes
    /// normally. The final text is <paramref name="delta1"/> + <paramref name="delta2"/>.
    /// </summary>
    public static ResumeCallBehavior MultiDeltaTextThenComplete(string delta1, string delta2)
        => new(ResumeCallBehaviorKind.MultiDeltaTextThenComplete, Delta1: delta1, Delta2: delta2);

    public static ResumeCallBehavior InstantToolCall(FunctionCallContent toolCall)
        => new(ResumeCallBehaviorKind.InstantToolCall, ToolCall: toolCall);
}

/// <summary>
/// Fake <see cref="IChatClient"/> with per-call scripted streaming behavior:
/// stall after a couple of substantive deltas (never completes), return text
/// instantly, or return a tool call instantly. Records every call's message list
/// and <see cref="ChatOptions"/> so tests can assert a resumed call re-sends the
/// identical prompt and tool surface.
/// </summary>
internal sealed class ResumeTestChatClient : IChatClient
{
    // Bounds every wait for the next streaming invocation so a regression in the
    // production resume/watchdog wiring fails the test with a clear
    // TimeoutException instead of hanging the test run indefinitely.
    private static readonly TimeSpan InvocationWaitTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private int _callCount;
    private readonly List<IReadOnlyList<ChatMessage>> _receivedMessages = [];
    private readonly List<ChatOptions?> _receivedOptions = [];
    private readonly Channel<int> _invocations =
        Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    public int CallCount => _callCount;

    public IReadOnlyList<IReadOnlyList<ChatMessage>> ReceivedMessages
    {
        get { lock (_gate) { return _receivedMessages.ToArray(); } }
    }

    public IReadOnlyList<ChatOptions?> ReceivedOptions
    {
        get { lock (_gate) { return _receivedOptions.ToArray(); } }
    }

    public Queue<ResumeCallBehavior> Behaviors { get; } = new();

    /// <summary>
    /// Awaits the next streaming invocation. The watchdog is already armed by
    /// then. Bounded by <see cref="InvocationWaitTimeout"/> — a regression that
    /// stops the actor from re-firing the call (e.g. resume silently not
    /// happening) fails with a <see cref="TimeoutException"/> instead of hanging.
    /// </summary>
    public async Task WaitForStreamInvocationAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(InvocationWaitTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await _invocations.Reader.ReadAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {InvocationWaitTimeout} waiting for the next streaming invocation " +
                $"(callCount so far: {_callCount}).");
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromException<ChatResponse>(new NotSupportedException("Streaming path only."));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        ResumeCallBehavior behavior;
        int callNumber;
        lock (_gate)
        {
            _receivedMessages.Add(messageList);
            _receivedOptions.Add(options);
            callNumber = ++_callCount;
            behavior = Behaviors.Count > 0
                ? Behaviors.Dequeue()
                : ResumeCallBehavior.InstantText($"[fake] default response #{callNumber}");
        }

        _invocations.Writer.TryWrite(callNumber);

        return behavior.Kind switch
        {
            ResumeCallBehaviorKind.StallAfterDeltas => StallAfterDeltasAsync(behavior.Delta1!, behavior.Delta2!),
            ResumeCallBehaviorKind.StallImmediately => TestStreamingHelpers.NeverCompletesAsync(cancellationToken),
            ResumeCallBehaviorKind.InstantText => TestStreamingHelpers.ReturnTextAsync(behavior.Text!, cancellationToken),
            ResumeCallBehaviorKind.MultiDeltaTextThenComplete => MultiDeltaTextThenCompleteAsync(behavior.Delta1!, behavior.Delta2!),
            ResumeCallBehaviorKind.InstantToolCall => InstantToolCallAsync(behavior.ToolCall!, cancellationToken),
            _ => throw new InvalidOperationException($"Unhandled behavior kind {behavior.Kind}")
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StallAfterDeltasAsync(string delta1, string delta2)
    {
        yield return new ChatResponseUpdate
        {
            Role = AiChatRole.Assistant,
            Contents = [new TextContent(delta1)]
        };
        await Task.Yield();

        yield return new ChatResponseUpdate
        {
            Contents = [new TextContent(delta2)]
        };
        await Task.Yield();

        // Stream is now silent — never completes on its own; the actor's watchdog
        // is the only thing that ends this turn.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.Task;
        yield break;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> MultiDeltaTextThenCompleteAsync(string delta1, string delta2)
    {
        yield return new ChatResponseUpdate
        {
            Role = AiChatRole.Assistant,
            Contents = [new TextContent(delta1)]
        };
        await Task.Yield();

        yield return new ChatResponseUpdate
        {
            Contents = [new TextContent(delta2)]
        };
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> InstantToolCallAsync(
        FunctionCallContent toolCall,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        var response = new ChatResponse(new ChatMessage(AiChatRole.Assistant, [toolCall]));
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
