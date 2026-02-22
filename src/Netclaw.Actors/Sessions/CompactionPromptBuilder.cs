using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Builds structured prompts for the compaction summarization LLM call.
/// Uses domain-specific sections rather than generic "summarize this" to
/// improve retention of critical context (per Factory.ai evaluation findings).
/// </summary>
public static class CompactionPromptBuilder
{
    /// <summary>
    /// Builds the system prompt for the compaction summarization call.
    /// </summary>
    public static string BuildSummarizationSystemPrompt()
    {
        return """
            You are a conversation summarizer. Your job is to compress a conversation
            history into a structured summary that preserves the most important context
            for continuing the conversation.

            Produce your summary in the following sections. Omit any section that has
            no relevant content.

            ## Task Overview
            What is the user working on? What is the high-level goal?

            ## Current State
            Where did the conversation leave off? What has been accomplished so far?

            ## Key Decisions
            What decisions were made during the conversation? Include rationale.

            ## Important Facts
            Key facts, names, paths, URLs, configuration values, or other specifics
            that would be needed to continue the work.

            ## Pending Actions
            What remains to be done? Any open questions or next steps?

            ## Tool Usage Summary
            Summarize tools that were used and their key outcomes. Do not reproduce
            full tool outputs — just the essential findings.

            Keep the summary concise but complete. Prioritize information that would
            be needed to continue the conversation without the user having to repeat
            themselves.
            """;
    }

    /// <summary>
    /// Builds the user prompt containing the conversation history to summarize.
    /// </summary>
    public static string BuildSummarizationUserPrompt(
        IReadOnlyList<SerializableChatMessage> history)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Summarize the following conversation history into the structured format described above.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in history)
        {
            // Skip system prompt — it's preserved separately
            if (msg.Role == ChatRole.System)
                continue;

            var roleLabel = msg.Role switch
            {
                ChatRole.User => "User",
                ChatRole.Assistant => "Assistant",
                ChatRole.Tool => $"Tool ({msg.Name ?? "unknown"})",
                _ => msg.Role.ToString()
            };

            sb.AppendLine($"**{roleLabel}:**");

            if (msg.ToolCalls.Count > 0)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    sb.AppendLine($"[Called tool: {tc.Name}]");
                }
            }

            if (!string.IsNullOrEmpty(msg.Content))
            {
                sb.AppendLine(msg.Content);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the system prompt for pre-compaction memory extraction.
    /// </summary>
    public static string BuildMemoryExtractionSystemPrompt()
    {
        return """
            You are a memory extraction assistant. Your job is to identify durable
            memories from a conversation that should be preserved long-term, beyond
            the current conversation context.

            Extract the following types of information:

            ## Key Facts
            Important facts learned during the conversation (names, preferences,
            configurations, decisions, constraints).

            ## Action Items
            Things the user needs to do or follow up on.

            ## Learned Preferences
            User preferences or working patterns observed during the conversation.

            Be concise. Only extract information that would be valuable in future
            conversations. Skip ephemeral details.
            """;
    }

    /// <summary>
    /// Builds the user prompt for memory extraction.
    /// </summary>
    public static string BuildMemoryExtractionUserPrompt(
        IReadOnlyList<SerializableChatMessage> history)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Extract durable memories from the following conversation:");
        sb.AppendLine();

        foreach (var msg in history)
        {
            if (msg.Role == ChatRole.System) continue;

            var roleLabel = msg.Role switch
            {
                ChatRole.User => "User",
                ChatRole.Assistant => "Assistant",
                ChatRole.Tool => $"Tool ({msg.Name ?? "unknown"})",
                _ => msg.Role.ToString()
            };

            if (!string.IsNullOrEmpty(msg.Content))
            {
                sb.AppendLine($"**{roleLabel}:** {msg.Content}");
            }
        }

        return sb.ToString();
    }
}
