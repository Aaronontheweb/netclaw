// -----------------------------------------------------------------------
// <copyright file="StreamingResponseReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Per-update diagnostics accumulated while consuming a streaming LLM response.
/// Both the session and sub-agent paths surface these counters in their logs.
/// </summary>
internal readonly record struct StreamDiagnostics(
    int UpdateCount,
    int EmptyUpdateCount,
    int TextDeltaCount,
    int TextChars,
    int ThinkingDeltaCount,
    int ThinkingChars,
    int ToolCallDeltaCount,
    string? FinishReason);

/// <summary>
/// Canonical classification of a single streaming update, computed once in the
/// reader so callers do not each re-derive "is this progress or a keepalive?".
/// <para>
/// A content-free keepalive (e.g. llama-server's <c>prompt_progress</c> heartbeat,
/// or a usage-only chunk) proves the socket and server are alive but carries no
/// model output. Distinguishing it from substantive deltas lets a two-phase
/// watchdog refresh the generous prefill budget on keepalives yet only promote
/// to the tighter inter-delta budget once real tokens arrive.
/// </para>
/// </summary>
internal readonly record struct StreamUpdateClassification(
    bool HasSubstantiveContent,
    bool IsFirstSubstantive,
    bool IsKeepalive,
    bool HasFinish);

/// <summary>
/// The fully consumed streaming response plus its accumulated diagnostics.
/// </summary>
internal readonly record struct StreamReadResult(
    ChatResponse Response,
    StreamDiagnostics Diagnostics);

/// <summary>
/// Single owner of the streaming LLM consumption loop shared by the main-session
/// (<see cref="SessionLlmInvoker"/>) and sub-agent (<c>SubAgentActor.InvokeLlmAsync</c>)
/// paths. It owns the <c>await foreach</c>, per-update counting, the final
/// <see cref="ChatResponse"/> assembly (with an empty-response fallback), and the
/// one canonical update classification. Callers supply <paramref name="onUpdate"/>
/// to adapt each classified update to their own actor messages (UI deltas, watchdog
/// pings) — that dispatch is the only legitimately path-specific part.
/// </summary>
internal static class StreamingResponseReader
{
    public static async Task<StreamReadResult> ReadAsync(
        IChatClient client,
        IEnumerable<AiChatMessage> messages,
        ChatOptions? options,
        Action<ChatResponseUpdate, StreamUpdateClassification, StreamDiagnostics> onUpdate,
        CancellationToken ct)
    {
        var updates = new List<ChatResponseUpdate>();
        var textBuilder = new StringBuilder();
        var thinkingBuilder = new StringBuilder();
        var toolCalls = new List<AIContent>();

        var updateCount = 0;
        var emptyUpdateCount = 0;
        var textDeltaCount = 0;
        var textChars = 0;
        var thinkingDeltaCount = 0;
        var thinkingChars = 0;
        var toolCallDeltaCount = 0;
        string? finishReason = null;
        var anySubstantiveSeen = false;

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
        {
            updates.Add(update);
            updateCount++;

            if (update.Contents.Count == 0 && update.FinishReason is null)
                emptyUpdateCount++;

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        textDeltaCount++;
                        textChars += text.Text!.Length;
                        textBuilder.Append(text.Text);
                        break;
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        thinkingDeltaCount++;
                        thinkingChars += reasoning.Text!.Length;
                        thinkingBuilder.Append(reasoning.Text);
                        break;
                    case FunctionCallContent:
                        toolCallDeltaCount++;
                        toolCalls.Add(content);
                        break;
                }
            }

            if (update.FinishReason is not null)
                finishReason = update.FinishReason.ToString();

            var classification = Classify(update, anySubstantiveSeen);
            if (classification.IsFirstSubstantive)
                anySubstantiveSeen = true;

            onUpdate(
                update,
                classification,
                new StreamDiagnostics(
                    updateCount,
                    emptyUpdateCount,
                    textDeltaCount,
                    textChars,
                    thinkingDeltaCount,
                    thinkingChars,
                    toolCallDeltaCount,
                    finishReason));
        }

        var response = updates.Count > 0
            ? updates.ToChatResponse()
            : new ChatResponse(BuildFallbackMessage(textBuilder, thinkingBuilder, toolCalls));

        if (response.Messages.Count == 0)
            response.Messages.Add(BuildFallbackMessage(textBuilder, thinkingBuilder, toolCalls));

        return new StreamReadResult(
            response,
            new StreamDiagnostics(
                updateCount,
                emptyUpdateCount,
                textDeltaCount,
                textChars,
                thinkingDeltaCount,
                thinkingChars,
                toolCallDeltaCount,
                finishReason));
    }

    /// <summary>
    /// The single classification rule. A keepalive is a content-free update (or one
    /// carrying only usage stats) with no finish reason — the socket is alive but the
    /// model produced nothing. Non-empty text/thinking and tool calls are substantive;
    /// a finish reason ends the call. <paramref name="anySubstantiveSeen"/> is the
    /// caller's running "have we seen real output yet?" flag, used to mark the first
    /// substantive delta so a two-phase watchdog knows when to promote.
    /// </summary>
    internal static StreamUpdateClassification Classify(ChatResponseUpdate update, bool anySubstantiveSeen)
    {
        var hasSubstantive = false;
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                case FunctionCallContent:
                    hasSubstantive = true;
                    break;
            }

            if (hasSubstantive)
                break;
        }

        return new StreamUpdateClassification(
            HasSubstantiveContent: hasSubstantive,
            IsFirstSubstantive: hasSubstantive && !anySubstantiveSeen,
            IsKeepalive: !hasSubstantive && update.FinishReason is null,
            HasFinish: update.FinishReason is not null);
    }

    private static AiChatMessage BuildFallbackMessage(
        StringBuilder text,
        StringBuilder thinking,
        List<AIContent> toolCalls)
    {
        // Mirrors the order both callers historically used when reconstructing a
        // response from accumulated content: tool calls, then thinking, then text.
        var contents = new List<AIContent>(toolCalls);
        if (thinking.Length > 0)
            contents.Add(new TextReasoningContent(thinking.ToString()));
        if (text.Length > 0)
            contents.Add(new TextContent(text.ToString()));

        return new AiChatMessage(ChatRole.Assistant, contents);
    }
}
