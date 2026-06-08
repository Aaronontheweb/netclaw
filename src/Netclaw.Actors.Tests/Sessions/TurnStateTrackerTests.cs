// -----------------------------------------------------------------------
// <copyright file="TurnStateTrackerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Covers <see cref="TurnStateTracker.EvaluateEmptyResponse"/>: a thinking-only
/// response (reasoning emitted, no final answer) must get a thinking-specific
/// nudge, and the post-tool path must tolerate several retries before failing
/// the turn.
/// </summary>
public sealed class TurnStateTrackerTests
{
    private const string ThinkingNudgeMarker = "only reasoning";

    public enum ToolPhase
    {
        BeforeAnyToolUse,
        AfterToolUse,
    }

    [Theory]
    [InlineData(ToolPhase.AfterToolUse, LlmResponseKind.ThinkingOnly)]
    [InlineData(ToolPhase.AfterToolUse, LlmResponseKind.Empty)]
    [InlineData(ToolPhase.BeforeAnyToolUse, LlmResponseKind.ThinkingOnly)]
    [InlineData(ToolPhase.BeforeAnyToolUse, LlmResponseKind.Empty)]
    public void EmptyResponse_NudgeMatchesResponseKind(ToolPhase phase, LlmResponseKind kind)
    {
        var tracker = new TurnStateTracker();
        if (phase == ToolPhase.AfterToolUse)
            tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        var action = tracker.EvaluateEmptyResponse(kind, truncated: false);

        var retry = Assert.IsType<EmptyResponseAction.Retry>(action);
        if (kind == LlmResponseKind.ThinkingOnly)
            Assert.Contains(ThinkingNudgeMarker, retry.NudgeText);
        else
            Assert.DoesNotContain(ThinkingNudgeMarker, retry.NudgeText);
    }

    [Fact]
    public void TruncatedThinkingOnly_GetsBrevityNudge_NotStopThinkingScold()
    {
        var tracker = new TurnStateTracker();
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        // A length-truncated thinking-only response was cut off, not refused —
        // it must get the brevity nudge, never the "stop thinking" scold.
        var truncated = Assert.IsType<EmptyResponseAction.Retry>(
            tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: true));
        Assert.Contains("cut off", truncated.NudgeText);
        Assert.DoesNotContain(ThinkingNudgeMarker, truncated.NudgeText);

        // A non-truncated thinking-only still gets the stop-thinking nudge.
        var normal = Assert.IsType<EmptyResponseAction.Retry>(
            tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false));
        Assert.Contains(ThinkingNudgeMarker, normal.NudgeText);
    }

    [Fact]
    public void CumulativeEmptyCeiling_SurvivesToolBatchResets()
    {
        // Regression for #1346: a model that interleaves genuine tool calls with
        // thinking-only responses resets the consecutive guard on every batch, so
        // only the cumulative ceiling can stop the loop.
        var tracker = new TurnStateTracker();
        const int max = 3;
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        for (var i = 0; i < max; i++)
        {
            // Simulate a tool batch clearing the consecutive empty-response guard.
            tracker.ResetEmptyResponseGuards();
            Assert.IsType<EmptyResponseAction.Retry>(
                tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false, maxEmptyResponsesPerTurn: max));
        }

        // Exceeding the cumulative ceiling escalates once (tools disabled)...
        tracker.ResetEmptyResponseGuards();
        Assert.IsType<EmptyResponseAction.RetryWithoutTools>(
            tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false, maxEmptyResponsesPerTurn: max));

        // ...and the next empty response fails the turn instead of looping forever.
        tracker.ResetEmptyResponseGuards();
        Assert.IsType<EmptyResponseAction.Fail>(
            tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false, maxEmptyResponsesPerTurn: max));
    }

    [Fact]
    public void ResetForNewTurn_ClearsCumulativeEmptyCeiling()
    {
        var tracker = new TurnStateTracker();
        const int max = 2;

        // Drive the cumulative ceiling to the escalation point.
        tracker.EvaluateEmptyResponse(LlmResponseKind.Empty, truncated: false, maxEmptyResponsesPerTurn: max);
        tracker.EvaluateEmptyResponse(LlmResponseKind.Empty, truncated: false, maxEmptyResponsesPerTurn: max);
        Assert.IsType<EmptyResponseAction.RetryWithoutTools>(
            tracker.EvaluateEmptyResponse(LlmResponseKind.Empty, truncated: false, maxEmptyResponsesPerTurn: max));

        tracker.ResetForNewTurn();

        // A fresh turn starts the cumulative counter over.
        Assert.IsType<EmptyResponseAction.Retry>(
            tracker.EvaluateEmptyResponse(LlmResponseKind.Empty, truncated: false, maxEmptyResponsesPerTurn: max));
    }

    [Fact]
    public void PostToolThinkingOnly_RetriesSeveralTimesBeforeFailing()
    {
        var tracker = new TurnStateTracker();
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        // The first three consecutive thinking-only responses retry.
        Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false));
        Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false));
        Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false));

        // Only the fourth fails the turn.
        Assert.IsType<EmptyResponseAction.Fail>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false));
    }

    [Fact]
    public void ParallelToolBatch_CountsAsOneIteration()
    {
        var tracker = new TurnStateTracker();

        var status = tracker.RecordToolCompletion(resultCount: 8, maxToolIterationsPerTurn: 30);

        Assert.IsType<ToolBudgetStatus.Ok>(status);
        Assert.Equal(1, tracker.ToolIterationCount);
        // ToolCallCount remains for telemetry only — it counts results, not iterations.
        Assert.Equal(8, tracker.ToolCallCount);
    }

    [Fact]
    public void MultipleSerialRounds_CountAsMultipleIterations()
    {
        var tracker = new TurnStateTracker();

        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        Assert.Equal(3, tracker.ToolIterationCount);
    }

    [Fact]
    public void ReachingIterationCap_ReturnsExhausted()
    {
        var tracker = new TurnStateTracker();
        const int cap = 4;

        // First (cap - 1) iterations stay below the limit.
        for (var i = 0; i < cap - 1; i++)
        {
            var status = tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: cap);
            Assert.IsNotType<ToolBudgetStatus.Exhausted>(status);
        }

        // The cap-th iteration hits the limit.
        var capped = tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: cap);
        Assert.IsType<ToolBudgetStatus.Exhausted>(capped);
        Assert.Equal(cap, tracker.ToolIterationCount);
    }

    [Fact]
    public void RawCallVolume_DoesNotControlTheLimit()
    {
        // 100 tool results delivered in a single iteration must NOT trigger the cap.
        var tracker = new TurnStateTracker();

        var status = tracker.RecordToolCompletion(resultCount: 100, maxToolIterationsPerTurn: 5);

        Assert.IsType<ToolBudgetStatus.Ok>(status);
        Assert.Equal(1, tracker.ToolIterationCount);
        Assert.Equal(100, tracker.ToolCallCount);
    }
}
