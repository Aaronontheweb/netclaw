// -----------------------------------------------------------------------
// <copyright file="TurnStateTracker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Owns tool-loop control flow decisions: budget tracking, duplicate detection,
/// empty-response retry logic, and force-no-tools state. The actor asks
/// "what should I do?" and the tracker answers based on accumulated state.
/// </summary>
internal sealed class TurnStateTracker
{
    private const int MaxPreToolEmptyRetries = 2;
    private const int MaxPostToolEmptyRetries = 3;
    private const int DuplicateToolThreshold = 3;
    private const double BudgetNudgeRatio = 0.75;

    // Nudge for a thinking-only response: the model emitted reasoning but no
    // final answer. Generic across providers — no provider-specific payload.
    private const string ThinkingOnlyNudge =
        "Your last response contained only reasoning and no reply to the user. "
        + "Stop thinking and write your answer now as a normal assistant message.";

    // A length-truncated response was cut off mid-output by the provider's token
    // ceiling — it did not refuse to answer, so the "stop thinking" scold is
    // counterproductive. Ask for brevity so the next attempt fits the budget.
    private const string TruncatedResponseNudge =
        "Your previous response was cut off before you finished — it reached the output length limit. "
        + "Give your final answer directly now and keep any reasoning brief.";

    private const string PreToolEmptyNudge =
        "Your previous response was empty. If you need MCP capabilities, call search_tools(\"servers\") to pick a server "
        + "(for example browser, memory, or email), then call search_tools(\"<intent>\", server: \"<server_name>\") to load tools. "
        + "MCP tools are not directly callable until loaded via search_tools.";

    private const string PostToolEmptyNudge =
        "You received tool results but did not respond. "
        + "Continue working or produce your final response.";

    // Sent once when the cumulative empty-response ceiling is reached: a final
    // tools-disabled attempt asking for a direct answer (mirrors the tool-budget
    // Exhausted escalation) before the turn is failed.
    private const string CumulativeEmptyEscalationNudge =
        "You have produced several responses this turn with no reply to the user. "
        + "Do NOT call any more tools. Write your final answer now as a normal assistant message, "
        + "using the information you have already gathered.";

    private const string EmptyResponseFailureMessage =
        "I didn't manage to produce a reply. Please try rephrasing or sending your request again.";

    private readonly Dictionary<ToolCallFingerprint, int> _toolCallCounts = [];

    public int ToolCallCount { get; private set; }
    public int ToolIterationCount { get; private set; }
    public bool ForceNoToolsActive { get; set; }

    private bool _budgetNudgeSent;
    private int _postToolEmptyResponseCount;
    private int _preToolEmptyResponseCount;
    private bool _duplicateNudgeSent;

    // Cumulative empty/thinking-only responses this turn. Unlike the consecutive
    // counters above, this is reset ONLY in ResetForNewTurn() — never in
    // ResetEmptyResponseGuards() or ResetToolCounters() — so a model that
    // interleaves genuine tool calls with empty responses still hits a ceiling
    // instead of spinning until MaxToolIterationsPerTurn. See issue #1346.
    private int _cumulativeEmptyResponsesThisTurn;
    private bool _emptyResponseEscalated;

    /// <summary>
    /// Reset all per-turn state. Called at the start of each user turn.
    /// </summary>
    public void ResetForNewTurn()
    {
        ToolCallCount = 0;
        ToolIterationCount = 0;
        _budgetNudgeSent = false;
        _postToolEmptyResponseCount = 0;
        _preToolEmptyResponseCount = 0;
        ForceNoToolsActive = false;
        _toolCallCounts.Clear();
        _duplicateNudgeSent = false;
        _cumulativeEmptyResponsesThisTurn = 0;
        _emptyResponseEscalated = false;
    }

    /// <summary>
    /// Partial reset for mid-turn buffer drain: clears tool counters and hashes
    /// but preserves empty-response and force-no-tools state.
    /// </summary>
    public void ResetToolCounters()
    {
        ToolCallCount = 0;
        ToolIterationCount = 0;
        _toolCallCounts.Clear();
        _duplicateNudgeSent = false;
    }

    /// <summary>
    /// Reset empty-response guards when the model starts doing tool work.
    /// Called when a new tool call batch is initiated — the model is clearly
    /// not stuck, so retry counters reset.
    /// </summary>
    public void ResetEmptyResponseGuards()
    {
        _postToolEmptyResponseCount = 0;
        _preToolEmptyResponseCount = 0;
        ForceNoToolsActive = false;
    }

    // ── Tool call tracking ──

    /// <summary>
    /// Record a tool call for duplicate detection.
    /// </summary>
    public void TrackToolCall(string toolName, string? argumentsJson)
    {
        var fingerprint = new ToolCallFingerprint(toolName, argumentsJson ?? "{}");
        _toolCallCounts.TryGetValue(fingerprint, out var count);
        _toolCallCounts[fingerprint] = count + 1;
    }

    // ── Tool budget decisions ──

    /// <summary>
    /// Record completed tool results and determine what the actor should do next.
    /// Call after tool execution completes with the number of results in the batch.
    /// Enforcement is iteration-based: one completed LLM-to-tools round increments
    /// <see cref="ToolIterationCount"/> by 1 regardless of how many tool calls
    /// were issued in parallel. <see cref="ToolCallCount"/> is retained for
    /// telemetry only.
    /// </summary>
    public ToolBudgetStatus RecordToolCompletion(int resultCount, int maxToolIterationsPerTurn)
    {
        ToolCallCount += resultCount;
        ToolIterationCount++;

        if (ToolIterationCount >= maxToolIterationsPerTurn)
        {
            return new ToolBudgetStatus.Exhausted(
                $"You have reached the tool iteration limit for this turn. "
                + "Do NOT request any more tools. "
                + "Summarize the work you completed and produce your final response "
                + "based on the information you have gathered so far. "
                + "If you could not complete the task, explain what you found and what remains.");
        }

        var budgetThreshold = (int)(maxToolIterationsPerTurn * BudgetNudgeRatio);
        if (ToolIterationCount >= budgetThreshold && !_budgetNudgeSent)
        {
            _budgetNudgeSent = true;
            var remaining = maxToolIterationsPerTurn - ToolIterationCount;
            return new ToolBudgetStatus.NudgeNeeded(
                remaining,
                $"You have used {ToolIterationCount} of {maxToolIterationsPerTurn} tool iterations for this turn. "
                + $"You have approximately {remaining} iterations remaining. "
                + "Start wrapping up your tool usage and prepare to produce your final response.");
        }

        return ToolBudgetStatus.Ok.Instance;
    }

    // ── Duplicate detection decisions ──

    /// <summary>
    /// Check for duplicate tool calls and return a nudge if the threshold is met.
    /// Returns null if no duplicates warrant a nudge.
    /// </summary>
    public DuplicateToolNudge? CheckForDuplicates()
    {
        if (_duplicateNudgeSent)
            return null;

        foreach (var (fingerprint, count) in _toolCallCounts)
        {
            if (count < DuplicateToolThreshold) continue;

            _duplicateNudgeSent = true;
            return new DuplicateToolNudge(
                fingerprint.ToolName, count,
                $"You have called the tool '{fingerprint.ToolName}' with the same arguments {count} times this turn. "
                + "This strongly indicates you are repeating work you already completed. "
                + "Review your prior tool results — the information you need is already in the conversation. "
                + "If the task is complete, produce your final response.");
        }

        return null;
    }

    // ── Empty response decisions ──

    /// <summary>
    /// The LLM produced no reply text and no tool calls. Determine what the
    /// actor should do. Expects <paramref name="kind"/> to be
    /// <see cref="LlmResponseKind.ThinkingOnly"/> or
    /// <see cref="LlmResponseKind.Empty"/>.
    /// <para>
    /// Two ceilings apply. The <em>consecutive</em> counters fast-fail a model
    /// that is stuck right now, but they are cleared by
    /// <see cref="ResetEmptyResponseGuards"/> on every tool batch — so a model
    /// that interleaves tool calls with empty responses evades them. The
    /// <em>cumulative</em> ceiling (<paramref name="maxEmptyResponsesPerTurn"/>),
    /// reset only at <see cref="ResetForNewTurn"/>, is the backstop that survives
    /// the per-batch reset. On reaching it, the turn escalates once (a final
    /// tools-disabled attempt) before failing.
    /// </para>
    /// <para>
    /// <paramref name="truncated"/> is true when the provider reported a
    /// length/token-limit finish reason. Such a response was cut off mid-output,
    /// not refused, so it gets a brevity nudge rather than the "stop thinking"
    /// scold.
    /// </para>
    /// </summary>
    public EmptyResponseAction EvaluateEmptyResponse(
        LlmResponseKind kind,
        bool truncated,
        int maxEmptyResponsesPerTurn)
    {
        _cumulativeEmptyResponsesThisTurn++;

        // Cumulative backstop — checked before the consecutive guards so an
        // interleaved tool/empty pattern (which keeps resetting those guards)
        // still terminates. A purely consecutive stall trips the tighter
        // consecutive caps below first, preserving fast-fail.
        if (_cumulativeEmptyResponsesThisTurn > maxEmptyResponsesPerTurn)
        {
            if (!_emptyResponseEscalated)
            {
                _emptyResponseEscalated = true;
                return new EmptyResponseAction.RetryWithoutTools(CumulativeEmptyEscalationNudge);
            }

            return new EmptyResponseAction.Fail(
                EmptyResponseFailureMessage,
                new InvalidOperationException(
                    $"LLM produced {_cumulativeEmptyResponsesThisTurn} empty or thinking-only responses "
                    + "this turn without producing a reply."));
        }

        // Pre-tool: LLM hasn't done any tool work yet
        if (ToolIterationCount == 0)
        {
            _preToolEmptyResponseCount++;
            if (_preToolEmptyResponseCount > MaxPreToolEmptyRetries)
                return new EmptyResponseAction.Fail(
                    EmptyResponseFailureMessage,
                    new InvalidOperationException("LLM produced repeated empty responses before any tool execution."));

            return new EmptyResponseAction.Retry(SelectNudge(kind, truncated, preTool: true));
        }

        // Post-tool: nudge the model to produce its final reply
        _postToolEmptyResponseCount++;
        if (_postToolEmptyResponseCount > MaxPostToolEmptyRetries)
            return new EmptyResponseAction.Fail(
                EmptyResponseFailureMessage,
                new InvalidOperationException("LLM produced repeated empty responses after tool execution."));

        return new EmptyResponseAction.Retry(SelectNudge(kind, truncated, preTool: false));
    }

    private static string SelectNudge(LlmResponseKind kind, bool truncated, bool preTool)
    {
        if (truncated)
            return TruncatedResponseNudge;
        if (kind == LlmResponseKind.ThinkingOnly)
            return ThinkingOnlyNudge;
        return preTool ? PreToolEmptyNudge : PostToolEmptyNudge;
    }
}

internal readonly record struct ToolCallFingerprint(string ToolName, string ArgumentsJson);

// ── Result types ──

/// <summary>Result of <see cref="TurnStateTracker.RecordToolCompletion"/>.</summary>
internal abstract record ToolBudgetStatus
{
    /// <summary>Under budget, continue normally.</summary>
    internal sealed record Ok : ToolBudgetStatus
    {
        public static readonly Ok Instance = new();
    }

    /// <summary>Approaching budget limit — inject a nudge.</summary>
    internal sealed record NudgeNeeded(int Remaining, string NudgeText) : ToolBudgetStatus;

    /// <summary>Budget exhausted — force text-only response.</summary>
    internal sealed record Exhausted(string NudgeText) : ToolBudgetStatus;
}

/// <summary>Result of <see cref="TurnStateTracker.CheckForDuplicates"/>.</summary>
internal sealed record DuplicateToolNudge(string ToolName, int Count, string NudgeText);

/// <summary>Result of <see cref="TurnStateTracker.EvaluateEmptyResponse"/>.</summary>
internal abstract record EmptyResponseAction
{
    /// <summary>Retry the LLM call with the given nudge text.</summary>
    internal sealed record Retry(string NudgeText) : EmptyResponseAction;

    /// <summary>
    /// Final escalation attempt: retry with the given nudge but with tools
    /// disabled, forcing the model toward a text answer before the turn fails.
    /// </summary>
    internal sealed record RetryWithoutTools(string NudgeText) : EmptyResponseAction;

    /// <summary>Fail the turn with the given error message and cause.</summary>
    internal sealed record Fail(string ErrorMessage, Exception Cause) : EmptyResponseAction;
}
