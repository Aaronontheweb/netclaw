// -----------------------------------------------------------------------
// <copyright file="TurnStateTrackerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

    [Theory]
    [InlineData(true, true, true)]     // post-tool, thinking-only → thinking nudge
    [InlineData(true, false, false)]   // post-tool, empty → generic nudge
    [InlineData(false, true, true)]    // pre-tool, thinking-only → thinking nudge
    [InlineData(false, false, false)]  // pre-tool, empty → generic nudge
    public void EmptyResponse_NudgeMatchesThinkingState(bool afterToolWork, bool hasThinking, bool expectThinkingNudge)
    {
        var tracker = new TurnStateTracker();
        if (afterToolWork)
            tracker.RecordToolCompletion(resultCount: 1, maxToolCallsPerTurn: 30);

        var action = tracker.EvaluateEmptyResponse(hasThinking);

        var retry = Assert.IsType<EmptyResponseAction.Retry>(action);
        if (expectThinkingNudge)
            Assert.Contains(ThinkingNudgeMarker, retry.NudgeText);
        else
            Assert.DoesNotContain(ThinkingNudgeMarker, retry.NudgeText);
    }

    [Fact]
    public void PostToolThinkingOnly_RetriesSeveralTimesBeforeFailing()
    {
        var tracker = new TurnStateTracker();
        tracker.RecordToolCompletion(resultCount: 1, maxToolCallsPerTurn: 30);

        // The first three consecutive thinking-only responses retry.
        Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(hasThinking: true));
        Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(hasThinking: true));
        Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(hasThinking: true));

        // Only the fourth fails the turn.
        Assert.IsType<EmptyResponseAction.Fail>(tracker.EvaluateEmptyResponse(hasThinking: true));
    }
}
