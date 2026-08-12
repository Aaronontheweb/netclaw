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
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
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
/// discard-and-resume mechanism, its retry budget, and the tool-dispatch safety
/// gate, using <see cref="LlmSessionTestBase.UseTestScheduler"/> so the watchdog
/// fires only on an explicit <see cref="LlmSessionTestBase.AdvanceScheduler"/> —
/// no wall-clock race.
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
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.InstantText("Resumed answer after timeout"));

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
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains(partialMarker, StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        AdvanceScheduler(FirstTokenTimeout);

        // Second call (the resume) completes cleanly — no ErrorOutput/TurnCompleted
        // in between, proving the turn never failed.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        var text = await subscriber.FishForMessageAsync<object>(
            m => m is TextOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        var finalText = Assert.IsType<TextOutput>(text);

        // The dead call's partial content must not leak into the final assistant
        // message — it was discarded, not appended to.
        Assert.Equal("Resumed answer after timeout", finalText.Text);
        Assert.DoesNotContain(partialMarker, finalText.Text, StringComparison.Ordinal);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        Assert.Equal(2, _chatClient.CallCount);

        // The resumed call re-issued the SAME messages as the dead call: identical
        // role/text sequence, proving no mutation and no extra user message.
        var deadCallMessages = _chatClient.ReceivedMessages[0];
        var resumedCallMessages = _chatClient.ReceivedMessages[1];
        Assert.Equal(deadCallMessages.Count, resumedCallMessages.Count);
        for (var i = 0; i < deadCallMessages.Count; i++)
        {
            Assert.Equal(deadCallMessages[i].Role, resumedCallMessages[i].Role);
            Assert.Equal(deadCallMessages[i].Text, resumedCallMessages[i].Text);
        }

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
    public async Task Timeout_after_tool_call_dispatched_does_not_resume()
    {
        // First call dispatches a tool call and completes normally.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.InstantToolCall(
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test query" })));
        // Second call — the post-tool follow-up — stalls and times out. The safety
        // gate must block resume because a tool call was already dispatched this turn.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("post-tool chunk one ", "post-tool chunk two"));

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

        var error = await subscriber.FishForMessageAsync<object>(
            m => m is ErrorOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        var errorOutput = Assert.IsType<ErrorOutput>(error);
        Assert.Equal(ErrorCategory.Timeout, errorOutput.Category);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);

        // Exactly 2 calls: the tool-call round and the stalled follow-up. No resume
        // (third call) was attempted because a tool call had already been dispatched.
        Assert.Equal(2, _chatClient.CallCount);
        Assert.Equal(1, _fakeToolExecutor.CallCount);
    }
}

/// <summary>
/// One configured behavior for a single <see cref="ResumeTestChatClient"/> call,
/// dequeued in call order.
/// </summary>
internal enum ResumeCallBehaviorKind { StallAfterDeltas, InstantText, InstantToolCall }

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

    public static ResumeCallBehavior InstantText(string text)
        => new(ResumeCallBehaviorKind.InstantText, Text: text);

    public static ResumeCallBehavior InstantToolCall(FunctionCallContent toolCall)
        => new(ResumeCallBehaviorKind.InstantToolCall, ToolCall: toolCall);
}

/// <summary>
/// Fake <see cref="IChatClient"/> with per-call scripted streaming behavior:
/// stall after a couple of substantive deltas (never completes), return text
/// instantly, or return a tool call instantly. Records every call's message list
/// so tests can assert a resumed call re-sends the identical prompt.
/// </summary>
internal sealed class ResumeTestChatClient : IChatClient
{
    private readonly object _gate = new();
    private int _callCount;
    private readonly List<IReadOnlyList<ChatMessage>> _receivedMessages = [];
    private readonly Channel<int> _invocations =
        Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    public int CallCount => _callCount;

    public IReadOnlyList<IReadOnlyList<ChatMessage>> ReceivedMessages
    {
        get { lock (_gate) { return _receivedMessages.ToArray(); } }
    }

    public Queue<ResumeCallBehavior> Behaviors { get; } = new();

    /// <summary>Awaits the next streaming invocation. The watchdog is already armed by then.</summary>
    public async Task WaitForStreamInvocationAsync(CancellationToken cancellationToken)
        => await _invocations.Reader.ReadAsync(cancellationToken);

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
            callNumber = ++_callCount;
            behavior = Behaviors.Count > 0
                ? Behaviors.Dequeue()
                : ResumeCallBehavior.InstantText($"[fake] default response #{callNumber}");
        }

        _invocations.Writer.TryWrite(callNumber);

        return behavior.Kind switch
        {
            ResumeCallBehaviorKind.StallAfterDeltas => StallAfterDeltasAsync(behavior.Delta1!, behavior.Delta2!),
            ResumeCallBehaviorKind.InstantText => TestStreamingHelpers.ReturnTextAsync(behavior.Text!, cancellationToken),
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
