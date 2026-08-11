// -----------------------------------------------------------------------
// <copyright file="ChatPresentationReducerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Cli.Tui;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ChatPresentationReducerTests
{
    private static readonly SessionId SessionId = new("test/chat");

    [Fact]
    public void Parallel_tool_results_update_only_their_call_rows()
    {
        var state = ChatPresentationState.Empty;
        state = Apply(state, ToolCall("call-a", "search", 1));
        state = Apply(state, ToolCall("call-b", "search", 2));

        var second = ChatPresentationReducer.Reduce(state, ToolResult("call-b", "result-b", 3));

        Assert.True(second.State.Tools.ContainsKey("call-a"));
        Assert.False(second.State.Tools.ContainsKey("call-b"));
        var secondBlock = Assert.Single(second.Effects.OfType<ChatPresentationEffect.Commit>()).Block;
        Assert.Equal("tool:call-b", secondBlock.Key);
        Assert.Contains("result-b", secondBlock.SemanticText, StringComparison.Ordinal);

        var first = ChatPresentationReducer.Reduce(second.State, ToolResult("call-a", "result-a", 4));

        Assert.Empty(first.State.Tools);
        Assert.Equal(["tool:call-b", "tool:call-a"],
            first.State.Transcript.Select(block => block.Key).ToArray());
    }

    [Fact]
    public void Parallel_same_name_subagents_keep_distinct_run_rows()
    {
        var state = ChatPresentationState.Empty;
        state = Apply(state, SubAgent("run-a", SubAgentPhase.Started, 1));
        state = Apply(state, SubAgent("run-b", SubAgentPhase.Started, 2));
        state = Apply(state, SubAgent("run-b", SubAgentPhase.Activity, 3, "reading"));

        Assert.Equal(2, state.SubAgents.Count);
        Assert.Equal("reading", state.SubAgents["run-b"].Phase);
        Assert.Equal("started", state.SubAgents["run-a"].Phase);

        state = Apply(state, SubAgent("run-a", SubAgentPhase.Completed, 4));

        Assert.False(state.SubAgents.ContainsKey("run-a"));
        Assert.True(state.SubAgents.ContainsKey("run-b"));
        Assert.Contains(state.Transcript, block => block.Key == "subagent:run-a");
    }

    [Fact]
    public void Transient_thought_and_tool_activity_stay_out_of_settled_transcript()
    {
        var state = Apply(ChatPresentationState.Empty, new ThinkingDeltaOutput("private step")
        {
            SessionId = SessionId,
            TimestampMs = 1
        });
        state = Apply(state, ToolCall("call-a", "search", 2));
        state = Apply(state, new ToolActivityOutput
        {
            SessionId = SessionId,
            TimestampMs = 3,
            CallId = new ToolCallId("call-a"),
            ToolName = new ToolName("search"),
            TurnId = new TurnId("turn-1"),
            Phase = "running",
            Summary = "query 1"
        });

        Assert.Empty(state.Transcript);
        Assert.Equal("private step", state.ThoughtText);
        Assert.Equal("running", state.Tools["call-a"].Phase);

        state = Apply(state, new ThinkingOutput("short reason")
        {
            SessionId = SessionId,
            TimestampMs = 4
        });

        var thought = Assert.Single(state.Transcript);
        Assert.Equal(ChatBlockKind.Thought, thought.Kind);
        Assert.Equal("short reason", thought.Detail);
    }

    [Fact]
    public void Usage_block_shows_reasoning_tokens_and_keeps_complete_detail()
    {
        var state = Apply(ChatPresentationState.Empty, new UsageOutput
        {
            SessionId = SessionId,
            TimestampMs = 10,
            InputTokens = 100,
            OutputTokens = 20,
            CachedInputTokens = 40,
            ReasoningTokens = 12,
            ContextWindowTokens = 1000,
            UsagePercent = 0.1,
            PromptMs = 18,
            PredictedPerSecond = 55
        });

        var usage = Assert.Single(state.Transcript);
        Assert.Contains("12 thought", usage.Summary, StringComparison.Ordinal);
        Assert.Contains("Cached input tokens: 40", usage.Detail, StringComparison.Ordinal);
        Assert.Contains("Speed: 55.0 tokens/s", usage.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_resume_prefers_structured_transcript_over_legacy_messages()
    {
        var state = Apply(ChatPresentationState.Empty, new SessionJoined
        {
            SessionId = SessionId,
            TimestampMs = 10,
            TurnCount = 1,
            RecentMessages = [new ChatMessageDto("assistant", "legacy text")],
            RecentTranscript =
            [
                new SessionTranscriptEntry
                {
                    Type = SessionTranscriptEntryTypes.Tool,
                    CallId = "call-1",
                    ToolName = "status",
                    Result = "healthy"
                }
            ]
        });

        Assert.DoesNotContain(state.Transcript, block => block.Summary == "legacy text");
        Assert.Contains(state.Transcript, block =>
            block.Kind == ChatBlockKind.Tool && block.SemanticText.Contains("healthy", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsupported_output_commits_a_visible_diagnostic()
    {
        var state = Apply(ChatPresentationState.Empty, new UnknownOutput
        {
            SessionId = SessionId,
            TimestampMs = 9
        });

        var diagnostic = Assert.Single(state.Transcript);
        Assert.Equal(ChatBlockKind.Diagnostic, diagnostic.Kind);
        Assert.Contains(nameof(UnknownOutput), diagnostic.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_turn_settles_incomplete_activity_as_diagnostics()
    {
        var state = Apply(ChatPresentationState.Empty, ToolCall("call-a", "search", 1));
        state = Apply(state, SubAgent("run-a", SubAgentPhase.Started, 2));

        state = Apply(state, new TurnCompleted
        {
            SessionId = SessionId,
            TimestampMs = 3,
            TurnNumber = new TurnNumber(1),
            Outcome = TurnOutcome.Failed
        });

        Assert.Empty(state.Tools);
        Assert.Empty(state.SubAgents);
        Assert.Equal(2, state.Transcript.Count(block => block.Kind == ChatBlockKind.Diagnostic));
    }

    private static ChatPresentationState Apply(ChatPresentationState state, SessionOutput output) =>
        ChatPresentationReducer.Reduce(state, output).State;

    private static ToolCallOutput ToolCall(string callId, string name, long timestamp) => new()
    {
        SessionId = SessionId,
        TimestampMs = timestamp,
        CallId = new ToolCallId(callId),
        ToolName = new ToolName(name),
        ArgumentsJson = $"{{\"call\":\"{callId}\"}}"
    };

    private static ToolResultOutput ToolResult(string callId, string result, long timestamp) => new()
    {
        SessionId = SessionId,
        TimestampMs = timestamp,
        CallId = new ToolCallId(callId),
        ToolName = new ToolName("search"),
        Result = result
    };

    private static SubAgentOutput SubAgent(
        string runId,
        SubAgentPhase phase,
        long timestamp,
        string? activityPhase = null) => new()
        {
            SessionId = SessionId,
            TimestampMs = timestamp,
            AgentName = new AgentName("reviewer"),
            Phase = phase,
            RunId = new SubAgentRunId(runId),
            ParentCallId = new ToolCallId("parent"),
            ActivityPhase = activityPhase,
            Success = true,
            Outcome = SubAgentRunOutcome.Completed,
            Duration = TimeSpan.FromSeconds(2)
        };

    private sealed record UnknownOutput : SessionOutput;
}
