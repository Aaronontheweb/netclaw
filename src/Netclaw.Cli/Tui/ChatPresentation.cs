// -----------------------------------------------------------------------
// <copyright file="ChatPresentation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tui;

internal enum ChatBlockKind
{
    System,
    User,
    Assistant,
    Thought,
    Tool,
    Parallel,
    SubAgent,
    Approval,
    File,
    Error,
    Usage,
    Compaction,
    Diagnostic
}

internal sealed record ChatPresentationBlock(
    string Key,
    ChatBlockKind Kind,
    string Label,
    string Summary,
    string SemanticText,
    long TimestampMs,
    string? TurnId = null,
    string? Detail = null,
    bool IsFailure = false);

internal sealed record ToolActivityPresentation(
    string CallId,
    string ToolName,
    string? ArgumentsJson,
    string Phase,
    string? Summary,
    long StartedAtMs,
    string? TurnId,
    string BatchId,
    int BatchSize);

internal sealed record SubAgentActivityPresentation(
    string RunId,
    string? ParentCallId,
    string AgentName,
    string Phase,
    string? Summary,
    long StartedAtMs,
    string? ActiveToolName);

internal sealed record ChatPresentationState
{
    public static readonly ChatPresentationState Empty = new();

    public ImmutableList<ChatPresentationBlock> Transcript { get; init; } = [];

    public ImmutableDictionary<string, ToolActivityPresentation> Tools { get; init; } =
        ImmutableDictionary<string, ToolActivityPresentation>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableDictionary<string, SubAgentActivityPresentation> SubAgents { get; init; } =
        ImmutableDictionary<string, SubAgentActivityPresentation>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableHashSet<string> CommittedToolBatches { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    public ImmutableQueue<ToolInteractionRequest> PendingApprovals { get; init; } =
        ImmutableQueue<ToolInteractionRequest>.Empty;

    public string AssistantText { get; init; } = string.Empty;

    public string ThoughtText { get; init; } = string.Empty;

    public int TurnNumber { get; init; } = 1;

    public string? CurrentTurnId { get; init; }

    public string? SessionTitle { get; init; }

    public double? ContextUsagePercent { get; init; }

    public bool HasJoined { get; init; }

    public bool IsProcessing { get; init; }

    public ToolInteractionRequest? PendingApproval =>
        PendingApprovals.IsEmpty ? null : PendingApprovals.Peek();
}

internal abstract record ChatPresentationEffect
{
    public sealed record Commit(ChatPresentationBlock Block) : ChatPresentationEffect;

    public sealed record RefreshLiveRegion : ChatPresentationEffect;

    public sealed record SetStatus(string Text) : ChatPresentationEffect;

    public sealed record ShowApproval(ToolInteractionRequest Request) : ChatPresentationEffect;

    public sealed record ClearApproval : ChatPresentationEffect;
}

internal sealed record ChatReduction(
    ChatPresentationState State,
    IReadOnlyList<ChatPresentationEffect> Effects);

internal static class ChatPresentationReducer
{
    public static ChatReduction Reduce(ChatPresentationState state, SessionOutput output)
    {
        var effects = new List<ChatPresentationEffect>();
        var next = output switch
        {
            SessionJoined joined => ReduceJoined(state, joined, effects),
            TextDeltaOutput textDelta => state with
            {
                AssistantText = state.AssistantText + textDelta.Delta
            },
            TextOutput text => CommitAssistant(state, text, effects),
            ThinkingDeltaOutput thoughtDelta => state with
            {
                ThoughtText = state.ThoughtText + thoughtDelta.Delta
            },
            ThinkingOutput thought => CommitThought(state, thought, effects),
            ToolCallOutput toolCall => StartTool(state, toolCall, effects),
            ToolActivityOutput activity => UpdateTool(state, activity),
            ToolResultOutput toolResult => CompleteTool(state, toolResult, effects),
            SubAgentOutput subAgent => ReduceSubAgent(state, subAgent, effects),
            UsageOutput usage => CommitUsage(state, usage, effects),
            ErrorOutput error => Commit(state, ErrorBlock(error, state.CurrentTurnId), effects),
            FileOutput file => Commit(state, FileBlock(file, state.CurrentTurnId), effects),
            CompactionOutput compaction => Commit(state, CompactionBlock(compaction, state.CurrentTurnId), effects),
            ToolInteractionRequest approval => ShowApproval(state, approval, effects),
            ApprovalOutcomeOutput approval => ResolveApproval(state, approval, effects),
            TurnCompleted completed => CompleteTurn(state, completed, effects),
            ProcessingStateOutput processing => state with { IsProcessing = processing.IsProcessing },
            SessionTitleOutput title => CommitTitle(state, title, effects),
            BufferFlush => FlushAssistant(state, output.TimestampMs, effects),
            _ => Commit(state, DiagnosticBlock(
                $"unsupported:{output.GetType().Name}:{output.TimestampMs}",
                $"Unsupported session output: {output.GetType().Name}",
                output.TimestampMs,
                state.CurrentTurnId), effects)
        };

        if (output is TextDeltaOutput or ThinkingDeltaOutput or ToolCallOutput or ToolActivityOutput
            or ProcessingStateOutput)
        {
            effects.Add(new ChatPresentationEffect.RefreshLiveRegion());
        }

        return new ChatReduction(next, effects);
    }

    public static ChatReduction RecordUserPrompt(
        ChatPresentationState state,
        string prompt,
        long timestampMs)
    {
        var block = new ChatPresentationBlock(
            $"turn:{state.TurnNumber}:user",
            ChatBlockKind.User,
            "YOU",
            prompt,
            $"YOU\n{prompt}",
            timestampMs,
            state.CurrentTurnId,
            prompt);
        return new ChatReduction(
            state with { Transcript = state.Transcript.Add(block) },
            [new ChatPresentationEffect.Commit(block)]);
    }

    private static ChatPresentationState ReduceJoined(
        ChatPresentationState state,
        SessionJoined joined,
        List<ChatPresentationEffect> effects)
    {
        if (state.HasJoined)
        {
            effects.Add(new ChatPresentationEffect.SetStatus("Reconnected"));
            return state;
        }

        state = state with { SessionTitle = joined.Title };

        if (joined.RecentTranscript is { Count: > 0 })
        {
            for (var index = 0; index < joined.RecentTranscript.Count; index++)
            {
                var entry = joined.RecentTranscript[index];
                if (entry is
                    {
                        Type: SessionTranscriptEntryTypes.Tool,
                        BatchSize: > 1,
                        BatchId.Length: > 0
                    }
                    && !state.CommittedToolBatches.Contains(entry.BatchId))
                {
                    state = CommitParallelGroup(
                        state,
                        entry.BatchId,
                        entry.BatchSize.Value,
                        entry.TimestampMs,
                        entry.TurnId,
                        effects);
                }

                state = Commit(state, ResumeBlock(entry, index), effects);
            }
        }
        else if (joined.RecentMessages is { Count: > 0 })
        {
            for (var index = 0; index < joined.RecentMessages.Count; index++)
            {
                var message = joined.RecentMessages[index];
                var kind = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                    ? ChatBlockKind.User
                    : ChatBlockKind.Assistant;
                var label = kind == ChatBlockKind.User ? "YOU" : "NETCLAW";
                state = Commit(state, new ChatPresentationBlock(
                    $"legacy:{index}:{message.Role}",
                    kind,
                    label,
                    message.Content,
                    $"{label}\n{message.Content}",
                    joined.TimestampMs,
                    Detail: message.Content), effects);
            }
        }

        effects.Add(new ChatPresentationEffect.SetStatus("Ready"));
        return state with
        {
            HasJoined = true,
            TurnNumber = joined.TurnCount + 1
        };
    }

    private static ChatPresentationState CommitTitle(
        ChatPresentationState state,
        SessionTitleOutput output,
        List<ChatPresentationEffect> effects)
    {
        var block = new ChatPresentationBlock(
            $"title:{output.TimestampMs}",
            ChatBlockKind.System,
            "TITLE",
            output.Title,
            $"Session title: {output.Title}",
            output.TimestampMs);
        return Commit(state with { SessionTitle = output.Title }, block, effects);
    }

    private static ChatPresentationState CommitUsage(
        ChatPresentationState state,
        UsageOutput output,
        List<ChatPresentationEffect> effects)
    {
        var usagePercent = output.UsagePercent;
        if (usagePercent is null && output.InputTokens is { } inputTokens && output.ContextWindowTokens > 0)
            usagePercent = (double)inputTokens / output.ContextWindowTokens;

        return Commit(
            state with { ContextUsagePercent = usagePercent },
            UsageBlock(output, state.CurrentTurnId),
            effects);
    }

    private static ChatPresentationState CommitAssistant(
        ChatPresentationState state,
        TextOutput output,
        List<ChatPresentationEffect> effects)
    {
        var text = string.IsNullOrEmpty(output.Text) ? state.AssistantText : output.Text;
        if (string.IsNullOrEmpty(text))
            return state;

        var block = new ChatPresentationBlock(
            $"turn:{state.TurnNumber}:assistant",
            ChatBlockKind.Assistant,
            "NETCLAW",
            text,
            $"NETCLAW\n{text}",
            output.TimestampMs,
            state.CurrentTurnId,
            text);
        return Commit(state with { AssistantText = string.Empty }, block, effects);
    }

    private static ChatPresentationState FlushAssistant(
        ChatPresentationState state,
        long timestampMs,
        List<ChatPresentationEffect> effects)
    {
        if (string.IsNullOrEmpty(state.AssistantText))
            return state;

        var block = new ChatPresentationBlock(
            $"turn:{state.TurnNumber}:assistant:preamble:{timestampMs}",
            ChatBlockKind.Assistant,
            "NETCLAW",
            state.AssistantText,
            $"NETCLAW\n{state.AssistantText}",
            timestampMs,
            state.CurrentTurnId,
            state.AssistantText);
        return Commit(state with { AssistantText = string.Empty }, block, effects);
    }

    private static ChatPresentationState CommitThought(
        ChatPresentationState state,
        ThinkingOutput output,
        List<ChatPresentationEffect> effects)
    {
        var text = string.IsNullOrEmpty(output.Text) ? state.ThoughtText : output.Text;
        if (string.IsNullOrWhiteSpace(text))
            return state with { ThoughtText = string.Empty };

        var block = new ChatPresentationBlock(
            $"turn:{state.TurnNumber}:thought:{output.TimestampMs}",
            ChatBlockKind.Thought,
            "THOUGHT",
            FirstLine(text),
            $"THOUGHT\n{text}",
            output.TimestampMs,
            state.CurrentTurnId,
            text);
        return Commit(state with { ThoughtText = string.Empty }, block, effects);
    }

    private static ChatPresentationState StartTool(
        ChatPresentationState state,
        ToolCallOutput output,
        List<ChatPresentationEffect> effects)
    {
        if (output.BatchSize > 1
            && output.BatchId.Length > 0
            && !state.CommittedToolBatches.Contains(output.BatchId))
        {
            state = CommitParallelGroup(
                state,
                output.BatchId,
                output.BatchSize,
                output.TimestampMs,
                state.CurrentTurnId,
                effects);
        }

        var tool = new ToolActivityPresentation(
            output.CallId.Value,
            output.ToolName.Value,
            output.ArgumentsJson,
            "queued",
            null,
            output.TimestampMs,
            state.CurrentTurnId,
            output.BatchId,
            output.BatchSize);
        return state with { Tools = state.Tools.SetItem(tool.CallId, tool) };
    }

    private static ChatPresentationState UpdateTool(ChatPresentationState state, ToolActivityOutput output)
    {
        var key = output.CallId.Value;
        var existing = state.Tools.TryGetValue(key, out var tool)
            ? tool
            : new ToolActivityPresentation(
                key,
                output.ToolName.Value,
                null,
                output.Phase,
                output.Summary,
                output.TimestampMs,
                output.TurnId.Value,
                string.Empty,
                1);
        return state with
        {
            CurrentTurnId = output.TurnId.Value,
            Tools = state.Tools.SetItem(key, existing with
            {
                Phase = output.Phase,
                Summary = output.Summary,
                TurnId = output.TurnId.Value
            })
        };
    }

    private static ChatPresentationState CompleteTool(
        ChatPresentationState state,
        ToolResultOutput output,
        List<ChatPresentationEffect> effects)
    {
        state.Tools.TryGetValue(output.CallId.Value, out var active);
        var detail = string.Join('\n', new[]
        {
            active?.ArgumentsJson is null ? null : $"Arguments: {active.ArgumentsJson}",
            $"Result: {output.Result}"
        }.Where(value => value is not null));
        var block = new ChatPresentationBlock(
            $"tool:{output.CallId.Value}",
            ChatBlockKind.Tool,
            "TOOL",
            $"✓ {output.ToolName.Value} · {FirstLine(output.Result)}",
            $"Tool: {output.ToolName.Value}\nCall: {output.CallId.Value}\n{detail}",
            output.TimestampMs,
            active?.TurnId ?? state.CurrentTurnId,
            detail);
        return Commit(state with { Tools = state.Tools.Remove(output.CallId.Value) }, block, effects);
    }

    private static ChatPresentationState ReduceSubAgent(
        ChatPresentationState state,
        SubAgentOutput output,
        List<ChatPresentationEffect> effects)
    {
        var key = output.RunId?.Value ?? $"legacy:{output.AgentName.Value}";
        if (output.Phase != Actors.SubAgents.SubAgentPhase.Completed)
        {
            var current = state.SubAgents.TryGetValue(key, out var active)
                ? active
                : new SubAgentActivityPresentation(
                    key,
                    output.ParentCallId?.Value,
                    output.AgentName.Value,
                    output.Phase.ToString().ToLowerInvariant(),
                    output.ActivitySummary,
                    output.TimestampMs,
                    null);
            var activeToolName = ActiveSubAgentTool(output.ActivityPhase) ?? current.ActiveToolName;
            if (output.ActivityPhase is "processing tool results" or "calling the model")
                activeToolName = null;
            return state with
            {
                SubAgents = state.SubAgents.SetItem(key, current with
                {
                    Phase = output.ActivityPhase ?? output.Phase.ToString().ToLowerInvariant(),
                    Summary = output.ActivitySummary,
                    ActiveToolName = activeToolName
                })
            };
        }

        var outcome = output.Outcome.ToString().ToLowerInvariant();
        var detail = $"Run: {key}\nOutcome: {outcome}\nDuration: {output.Duration.TotalSeconds:F1}s"
                     + (output.OutcomeReason is null ? string.Empty : $"\nReason: {output.OutcomeReason.Value.Value}")
                     + (output.MemoryDecision is null ? string.Empty : $"\nMemory: {output.MemoryDecision}");
        var block = new ChatPresentationBlock(
            $"subagent:{key}",
            ChatBlockKind.SubAgent,
            "AGENT",
            $"{output.AgentName.Value} · {outcome} · {output.Duration.TotalSeconds:F1}s",
            $"Sub-agent: {output.AgentName.Value}\n{detail}",
            output.TimestampMs,
            state.CurrentTurnId,
            detail,
            output.Outcome == SubAgentRunOutcome.Failed);
        return Commit(state with { SubAgents = state.SubAgents.Remove(key) }, block, effects);
    }

    private static string? ActiveSubAgentTool(string? phase)
    {
        const string prefix = "running tools: ";
        if (phase is null || !phase.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        return phase[prefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static ChatPresentationState ShowApproval(
        ChatPresentationState state,
        ToolInteractionRequest approval,
        List<ChatPresentationEffect> effects)
    {
        effects.Add(new ChatPresentationEffect.ShowApproval(approval));
        effects.Add(new ChatPresentationEffect.SetStatus("Approval required"));
        effects.Add(new ChatPresentationEffect.RefreshLiveRegion());
        return state with { PendingApprovals = state.PendingApprovals.Enqueue(approval) };
    }

    private static ChatPresentationState ResolveApproval(
        ChatPresentationState state,
        ApprovalOutcomeOutput output,
        List<ChatPresentationEffect> effects)
    {
        var requester = state.SubAgents.Values.FirstOrDefault(run =>
            output.ParentCallId.Length > 0
            && string.Equals(run.ParentCallId, output.ParentCallId, StringComparison.Ordinal));
        var path = requester is null
            ? output.ParentCallId.Length > 0
                ? $"sub-agent › {output.ToolName.Value}"
                : output.ToolName.Value
            : $"{requester.AgentName} › {output.ToolName.Value}";
        var decision = ApprovalDecisionText(output.SelectedKey.Value);
        var detail = $"Tool: {output.ToolName.Value}\nCall: {output.CallId.Value}"
                     + (output.ParentCallId.Length == 0 ? string.Empty : $"\nParent call: {output.ParentCallId}")
                     + $"\nDecision: {decision}";
        var block = new ChatPresentationBlock(
            $"approval:{output.CallId.Value}:{output.TimestampMs}",
            ChatBlockKind.Approval,
            "APPROVAL",
            $"{path} · {decision}",
            $"Approval: {path}\n{detail}",
            output.TimestampMs,
            state.CurrentTurnId,
            detail,
            string.Equals(output.SelectedKey.Value, ApprovalOptionKeys.Deny, StringComparison.Ordinal));
        var remaining = ImmutableQueue.CreateRange(state.PendingApprovals.Where(request =>
            !string.Equals(request.CallId.Value, output.CallId.Value, StringComparison.Ordinal)));
        state = Commit(state with { PendingApprovals = remaining }, block, effects);
        effects.Add(new ChatPresentationEffect.SetStatus(
            remaining.IsEmpty ? "Generating..." : "Approval required"));
        effects.Add(new ChatPresentationEffect.RefreshLiveRegion());
        return state;
    }

    private static ChatPresentationState CompleteTurn(
        ChatPresentationState state,
        TurnCompleted completed,
        List<ChatPresentationEffect> effects)
    {
        state = FlushAssistant(state, completed.TimestampMs, effects);
        if (!string.IsNullOrWhiteSpace(state.ThoughtText))
        {
            state = CommitThought(state, new ThinkingOutput(state.ThoughtText)
            {
                SessionId = completed.SessionId,
                TimestampMs = completed.TimestampMs
            }, effects);
        }

        foreach (var tool in state.Tools.Values.OrderBy(tool => tool.StartedAtMs))
        {
            state = Commit(state, DiagnosticBlock(
                $"tool:{tool.CallId}:incomplete",
                $"Tool '{tool.ToolName}' ended without a settled result.",
                completed.TimestampMs,
                tool.TurnId), effects);
        }

        foreach (var subAgent in state.SubAgents.Values.OrderBy(run => run.StartedAtMs))
        {
            state = Commit(state, DiagnosticBlock(
                $"subagent:{subAgent.RunId}:incomplete",
                $"Sub-agent '{subAgent.AgentName}' ended without a terminal event.",
                completed.TimestampMs,
                state.CurrentTurnId), effects);
        }

        effects.Add(new ChatPresentationEffect.ClearApproval());
        effects.Add(new ChatPresentationEffect.SetStatus("Ready"));
        effects.Add(new ChatPresentationEffect.RefreshLiveRegion());
        return state with
        {
            Tools = state.Tools.Clear(),
            SubAgents = state.SubAgents.Clear(),
            PendingApprovals = ImmutableQueue<ToolInteractionRequest>.Empty,
            IsProcessing = false,
            TurnNumber = Math.Max(state.TurnNumber + 1, completed.TurnNumber.Value + 1),
            CurrentTurnId = null
        };
    }

    private static ChatPresentationState Commit(
        ChatPresentationState state,
        ChatPresentationBlock block,
        List<ChatPresentationEffect> effects)
    {
        effects.Add(new ChatPresentationEffect.Commit(block));
        return state with { Transcript = state.Transcript.Add(block) };
    }

    private static ChatPresentationBlock ResumeBlock(SessionTranscriptEntry entry, int index)
    {
        var kind = entry.Type switch
        {
            SessionTranscriptEntryTypes.User => ChatBlockKind.User,
            SessionTranscriptEntryTypes.Assistant => ChatBlockKind.Assistant,
            SessionTranscriptEntryTypes.Tool => ChatBlockKind.Tool,
            SessionTranscriptEntryTypes.Approval => ChatBlockKind.Approval,
            SessionTranscriptEntryTypes.SubAgent => ChatBlockKind.SubAgent,
            SessionTranscriptEntryTypes.File => ChatBlockKind.File,
            SessionTranscriptEntryTypes.Error => ChatBlockKind.Error,
            SessionTranscriptEntryTypes.Usage => ChatBlockKind.Usage,
            SessionTranscriptEntryTypes.Compaction => ChatBlockKind.Compaction,
            _ => ChatBlockKind.Diagnostic
        };
        var label = Label(kind);
        var summary = kind switch
        {
            ChatBlockKind.User or ChatBlockKind.Assistant => entry.Text ?? string.Empty,
            ChatBlockKind.Tool => $"✓ {entry.ToolName ?? "unknown"} · {FirstLine(entry.Result)}",
            ChatBlockKind.Approval => $"{entry.ToolName ?? "unknown"} · {ApprovalDecisionText(entry.ApprovalSelectedKey)}",
            ChatBlockKind.SubAgent => $"{entry.AgentName ?? "sub-agent"} · {entry.Outcome ?? "complete"}",
            ChatBlockKind.File => $"{entry.FileName ?? "file"} · {entry.FilePath}",
            ChatBlockKind.Error => entry.ErrorMessage ?? "Unknown error",
            ChatBlockKind.Usage => UsageSummary(entry),
            ChatBlockKind.Compaction => $"{entry.MessagesBefore ?? 0} → {entry.MessagesAfter ?? 0} messages",
            _ => entry.Text ?? $"Unsupported transcript entry: {entry.Type}"
        };
        var detail = ResumeDetail(entry);
        var identity = entry.CallId ?? entry.RunId ?? entry.TurnId ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ChatPresentationBlock(
            $"resume:{entry.Type}:{identity}:{index}",
            kind,
            label,
            summary,
            $"{label}\n{detail}",
            entry.TimestampMs,
            entry.TurnId,
            detail,
            kind == ChatBlockKind.Error || string.Equals(entry.Outcome, "failed", StringComparison.Ordinal));
    }

    private static ChatPresentationBlock UsageBlock(UsageOutput usage, string? turnId)
    {
        var summary = $"{usage.InputTokens ?? 0} in · {usage.OutputTokens ?? 0} out"
                      + (usage.ReasoningTokens is > 0 ? $" · {usage.ReasoningTokens} thought" : string.Empty)
                      + (usage.UsagePercent is not null ? $" · {usage.UsagePercent:P0} context" : string.Empty);
        var detail = $"Input tokens: {usage.InputTokens ?? 0}\nOutput tokens: {usage.OutputTokens ?? 0}"
                     + $"\nCached input tokens: {usage.CachedInputTokens ?? 0}"
                     + $"\nReasoning tokens: {usage.ReasoningTokens ?? 0}"
                     + (usage.PromptMs is null ? string.Empty : $"\nPrompt time: {usage.PromptMs:F1} ms")
                     + (usage.PredictedPerSecond is null ? string.Empty : $"\nSpeed: {usage.PredictedPerSecond:F1} tokens/s");
        return new ChatPresentationBlock(
            $"usage:{usage.TimestampMs}",
            ChatBlockKind.Usage,
            "USAGE",
            summary,
            $"Usage\n{detail}",
            usage.TimestampMs,
            turnId,
            detail);
    }

    private static ChatPresentationBlock ErrorBlock(ErrorOutput error, string? turnId)
    {
        var detail = $"Category: {error.Category}\nCorrelation: {error.CorrelationId:D}"
                     + (error.Cause is null ? string.Empty : $"\n{error.Cause}");
        return new ChatPresentationBlock(
            $"error:{error.CorrelationId:D}",
            ChatBlockKind.Error,
            "ERROR",
            error.Message,
            $"Error: {error.Message}\n{detail}",
            error.TimestampMs,
            turnId,
            detail,
            true);
    }

    private static ChatPresentationBlock FileBlock(FileOutput file, string? turnId)
    {
        var detail = $"Name: {file.FileName}\nType: {file.MimeType.Value}\nPath: {file.FilePath}";
        return new ChatPresentationBlock(
            $"file:{file.TimestampMs}:{file.FilePath}",
            ChatBlockKind.File,
            "FILE",
            $"{file.FileName} · {file.MimeType.Value}",
            detail,
            file.TimestampMs,
            turnId,
            detail);
    }

    private static ChatPresentationBlock CompactionBlock(CompactionOutput output, string? turnId)
    {
        var detail = $"Messages: {output.MessagesBefore} → {output.MessagesAfter}"
                     + $"\nTool results cleared: {output.ToolResultsCleared}"
                     + $"\nSummary created: {output.Summarized}"
                     + $"\nInput tokens: {output.PreCompactionInputTokens}"
                     + $"\nKeep count: {output.KeepCountUsed}";
        return new ChatPresentationBlock(
            $"compaction:{output.TimestampMs}",
            ChatBlockKind.Compaction,
            "CONTEXT",
            $"{output.MessagesBefore} → {output.MessagesAfter} messages",
            $"Context compaction\n{detail}",
            output.TimestampMs,
            turnId,
            detail);
    }

    private static ChatPresentationBlock DiagnosticBlock(
        string key,
        string text,
        long timestampMs,
        string? turnId) => new(
        key,
        ChatBlockKind.Diagnostic,
        "DIAGNOSTIC",
        text,
        text,
        timestampMs,
        turnId,
        text,
        true);

    private static string ResumeDetail(SessionTranscriptEntry entry) => entry.Type switch
    {
        SessionTranscriptEntryTypes.User or SessionTranscriptEntryTypes.Assistant => entry.Text ?? string.Empty,
        SessionTranscriptEntryTypes.Tool => $"Tool: {entry.ToolName ?? "unknown"}\nCall: {entry.CallId ?? "unknown"}"
                                            + (entry.ArgumentsJson is null ? string.Empty : $"\nArguments: {entry.ArgumentsJson}")
                                            + $"\nResult: {entry.Result ?? string.Empty}",
        SessionTranscriptEntryTypes.Approval => $"Tool: {entry.ToolName ?? "unknown"}\nCall: {entry.CallId ?? "unknown"}"
                                                + (string.IsNullOrEmpty(entry.ParentCallId)
                                                    ? string.Empty
                                                    : $"\nParent call: {entry.ParentCallId}")
                                                + $"\nDecision: {ApprovalDecisionText(entry.ApprovalSelectedKey)}",
        SessionTranscriptEntryTypes.SubAgent => $"Agent: {entry.AgentName ?? "unknown"}\nRun: {entry.RunId ?? "unknown"}"
                                                + $"\nOutcome: {entry.Outcome ?? "unknown"}"
                                                + (entry.OutcomeReason is null ? string.Empty : $"\nReason: {entry.OutcomeReason}"),
        SessionTranscriptEntryTypes.File => $"Name: {entry.FileName}\nType: {entry.MimeType}\nPath: {entry.FilePath}",
        SessionTranscriptEntryTypes.Error => $"Error: {entry.ErrorMessage}\nCategory: {entry.ErrorCategory}"
                                             + $"\nCorrelation: {entry.ErrorCorrelationId}"
                                             + (entry.ErrorDetail is null ? string.Empty : $"\n{entry.ErrorDetail}"),
        SessionTranscriptEntryTypes.Usage => UsageSummary(entry),
        SessionTranscriptEntryTypes.Compaction => $"Messages: {entry.MessagesBefore ?? 0} → {entry.MessagesAfter ?? 0}",
        _ => entry.Text ?? $"Unsupported transcript entry: {entry.Type}"
    };

    private static string UsageSummary(SessionTranscriptEntry entry) =>
        $"{entry.InputTokens ?? 0} in · {entry.OutputTokens ?? 0} out"
        + (entry.ReasoningTokens is > 0 ? $" · {entry.ReasoningTokens} thought" : string.Empty);

    private static string Label(ChatBlockKind kind) => kind switch
    {
        ChatBlockKind.User => "YOU",
        ChatBlockKind.Assistant => "NETCLAW",
        ChatBlockKind.Thought => "THOUGHT",
        ChatBlockKind.Tool => "TOOL",
        ChatBlockKind.Parallel => "PARALLEL",
        ChatBlockKind.SubAgent => "AGENT",
        ChatBlockKind.Approval => "APPROVAL",
        ChatBlockKind.File => "FILE",
        ChatBlockKind.Error => "ERROR",
        ChatBlockKind.Usage => "USAGE",
        ChatBlockKind.Compaction => "CONTEXT",
        _ => "DIAGNOSTIC"
    };

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "complete";

        var end = text.IndexOfAny(['\r', '\n']);
        var first = end < 0 ? text : text[..end];
        return first.Length <= 100 ? first : string.Concat(first.AsSpan(0, 97), "...");
    }

    private static ChatPresentationState CommitParallelGroup(
        ChatPresentationState state,
        string batchId,
        int batchSize,
        long timestampMs,
        string? turnId,
        List<ChatPresentationEffect> effects)
    {
        var block = new ChatPresentationBlock(
            $"parallel:{batchId}",
            ChatBlockKind.Parallel,
            "PARALLEL",
            $"{batchSize} tool calls",
            $"Parallel tool batch: {batchId}\nCalls: {batchSize}",
            timestampMs,
            turnId,
            $"Batch: {batchId}\nCalls: {batchSize}");
        return Commit(
            state with { CommittedToolBatches = state.CommittedToolBatches.Add(batchId) },
            block,
            effects);
    }

    private static string ApprovalDecisionText(string? selectedKey) => selectedKey switch
    {
        ApprovalOptionKeys.ApproveOnce => "approved once",
        ApprovalOptionKeys.ApproveSession => "approved for this chat",
        ApprovalOptionKeys.ApproveAlways => "approved for this directory",
        ApprovalOptionKeys.ApproveEverywhere => "approved everywhere",
        ApprovalOptionKeys.Deny => "denied",
        _ => "resolved"
    };
}
