# Context Management Patterns Across LLM Agent SDKs

Date: 2026-02-22
Task: Pre-implementation research for session actor context management

Research across OpenAI Agents SDK, LangChain, Semantic Kernel, AutoGen, Anthropic
Claude SDK, Google ADK, LlamaIndex, CrewAI, and Haystack. Supplemented by
independent evaluations from JetBrains Research and Factory.ai.

---

## 1. Context Window Compaction

### 1.1 The Dominant Pattern: Summary Prefix + Recent Raw Messages

Nearly every framework converges on the same strategy: keep recent messages
verbatim, summarize everything older into a running summary prepended to
the conversation.

- **LangChain** `ConversationSummaryBufferMemory`: token-count threshold
  (default 2k). Oldest messages folded into a `moving_summary_buffer`
  incrementally. Summary stored as a `SystemMessage`.
- **Semantic Kernel** `ChatHistorySummarizationReducer`: message-count
  threshold (`target_count + threshold_count`). Structured summarization
  with configurable instructions. Function call pairs handled explicitly.
- **LlamaIndex** `ChatSummaryMemoryBuffer`: token-count ratio of context
  window. Iterative summarization of overflow messages.
- **Anthropic SDK** `tool_runner`: token-count threshold (default 100k).
  Replaces entire history with structured summary in `<summary>` tags.
  Supports custom summary prompts and cheaper compaction model.
- **Google ADK**: event-interval trigger with sliding overlap. Overlap
  ensures continuity between compaction windows.

### 1.2 Trigger Mechanisms

| Framework | Trigger Type | Default |
|-----------|-------------|---------|
| LangChain | Token count | 2,000 |
| Semantic Kernel | Message count | target + threshold |
| LlamaIndex | Token ratio of context window | LLM-derived |
| Anthropic SDK | Token count | 100,000 |
| OpenAI server | Token count | configurable |
| Google ADK | Event interval | configurable |
| AutoGen | Message count or token count | configurable |

**Recommendation for Netclaw**: Token-count threshold, derived from
`SessionConfig.ContextWindowTokens * CompactionThreshold`. The LLM response's
`UsageDetails.InputTokenCount` is the most reliable proxy for current context
consumption.

### 1.3 Before Summarization: Clear Tool Results First

JetBrains Research (NeurIPS 2025, 500 SWE-bench instances) found that
**observation masking** — replacing old tool outputs with placeholders while
keeping reasoning/action history — matched or beat LLM summarization in 4 of
5 settings:

- Both achieved >50% cost reduction vs. unmanaged contexts
- LLM summarization caused 13-15% longer agent runs (trajectory elongation)
- Summary generation consumed >7% of total costs
- **Recommendation**: Use observation masking as primary defense; selectively
  incorporate summarization

Anthropic's own recommended hierarchy:
1. Tool result clearing (cheapest, reversible)
2. Compaction / summarization (lossy but structured)
3. Structured note-taking (external memory)
4. Sub-agent isolation (each gets own context window)

**Recommendation for Netclaw**: Implement a tiered approach. Phase 1: clear
old tool results (keep the tool call name + a "result cleared" placeholder).
Phase 2: if still over threshold, run structured summarization.

### 1.4 Structured > Generic Summarization

Factory.ai evaluated three approaches on 36,000+ production messages:

| Method | Score | Approach |
|--------|-------|----------|
| Factory (structured) | 3.70/5.0 | Anchored iterative with explicit sections |
| Anthropic SDK | 3.44/5.0 | Full regeneration per cycle |
| OpenAI compact | 3.35/5.0 | Opaque, 99.3% compression |

Key findings:
- Explicit sections (task overview, current state, decisions, next steps)
  outperform generic "summarize this" prompts
- **Anchored iterative merging** (update existing summary) beats full
  regeneration — preserves details that would otherwise be lost
- Domain-specific section headings improve retention of critical context

**Recommendation for Netclaw**: Design a structured compaction prompt template
with sections tailored to our use cases (homelab operations context, prior
tool usage, user preferences, pending tasks).

### 1.5 Compaction Model Selection

| Framework | Default | Configurable? |
|-----------|---------|---------------|
| LangChain | Same LLM | Pass different instance |
| Semantic Kernel | Same ChatCompletionService | Pass different instance |
| Anthropic SDK | Same model | Explicit `compaction_control.model` |
| Google ADK | Agent's model | Explicit `LlmEventSummarizer(llm=...)` |
| LlamaIndex | Same LLM | Pass different instance |

**Recommendation for Netclaw**: Add optional `CompactionModelId` to
`SessionConfig`. Default to the session's model. Allow routing compaction
through a cheaper/faster model when configured. This maps to resolving a
different `IChatClient` from a provider factory.

### 1.6 System Prompt Handling During Compaction

**Universal**: Every framework preserves the system prompt. It's either
explicitly preserved (Semantic Kernel documents this as critical to avoid
"unpredictable LLM behaviour") or architecturally separate (Anthropic/OpenAI
treat it as a distinct API parameter).

**Recommendation for Netclaw**: System prompt is always slot 0 in
`SessionState.History`. Compaction logic must never remove it. Already
implemented in `SessionState.Apply(SessionCompacted)` — verified by tests.

---

## 2. Tool Call/Result Pair Integrity

### The Problem

When compacting or truncating conversation history, tool calls and their
results form logical pairs. Orphaning a tool call from its result (or vice
versa) creates broken context that confuses the LLM.

### Framework Approaches

- **Semantic Kernel**: Explicitly skips orphaned function-calling sequences
  during reduction. By default excludes function call content from
  summarization (`include_function_content_in_summary=False`). Only the LLM's
  interpretation of tool results gets included in the summary.
- **Anthropic context editing**: Clears tool *results* but preserves the
  tool *call* structure. Configurable `keep` parameter (default: 3 recent
  tool uses preserved). Option to exclude specific tools from clearing.
- **Manus framework**: Most recent tool calls always kept in full detail.
  Older tool interactions compressed to just the tool name + outcome.

### Recommendation for Netclaw

Our `SerializableChatMessage` is currently flat (role + content). When tool
execution is added, we need to either:

1. Store tool call/result as paired entries with a shared `CallId`, or
2. Store them as a single compound event (`ToolTurnRecorded`) that keeps
   the pair atomic

The compaction logic should:
- Never orphan a tool call from its result
- Clear old tool results first (replace with placeholder) before summarizing
- Keep the N most recent tool interactions in full detail
- Summarize older tool interactions as "Used {tool} for {purpose} → {outcome}"

---

## 3. Memory Architecture Beyond Conversation History

### The Pattern: Tiered Memory

Several frameworks distinguish between conversation history (ephemeral,
context-window-bound) and durable memory (persisted externally):

- **CrewAI**: Short-term (current task), long-term (past executions), entity
  (accumulated facts), contextual (combined for current prompt)
- **LlamaIndex**: Chat history (buffer) + vector memory (embeddings for
  long-term retrieval)
- **Haystack + Mem0**: Intelligent fact extraction from conversations into
  optimized memory representations
- **Anthropic recommendation**: Structured note-taking as a compaction
  alternative — agent writes key facts to external memory

### Pre-Compaction Memory Flush

Our OpenSpec already specifies this (netclaw-session spec): "Before compaction
runs, the system SHALL trigger a pre-compaction memory flush that persists
durable memories (key facts, decisions, action items) to local memory files
so they survive context reset."

This aligns with the Anthropic hierarchy and CrewAI's tiered approach. The
session actor should:

1. Detect compaction threshold approaching
2. Fire a "memory extraction" LLM call (structured prompt asking for key
   facts, decisions, action items)
3. Persist extracted memories to external storage (MCP memorizer or local files)
4. Then run the actual compaction

### Recommendation for Netclaw

The memory flush is already spec'd. Implementation should use a structured
extraction prompt with explicit sections matching what the compaction summary
will lose. This is a separate LLM call from the compaction summarization.

---

## 4. Context Window Usage Tracking and Subscriber Notification

### The Pattern: Usage Transparency

No framework hides context window consumption from the caller. Most expose
it directly:

- **OpenAI**: `usage` object in every response
- **Anthropic**: `usage` object with `input_tokens`, `output_tokens`,
  `cache_creation_input_tokens`, `cache_read_input_tokens`
- **Semantic Kernel**: Reducers operate on message count/token count which
  is visible to the caller

### Recommendation for Netclaw

Enrich `UsageOutput` with context window metadata so subscribers can display
progress without duplicating config:

- `ContextWindowTokens` (total capacity from config)
- `UsagePercent` (computed: input tokens / context window)
- Let compaction threshold and approaching-limit UX be subscriber concerns

The actor tracks raw numbers; subscribers decide how to present them. No
separate "compaction warning" output type needed.

---

## 5. Behavior State Design for Compaction

### The Problem

Compaction involves one or two LLM calls (memory extraction + summarization)
that are async, just like regular turns. The actor can't process new user
messages while compacting.

### Proposed Behavior States

```
Ready → (user message) → Processing → (LLM response) → [threshold check]
                                                              ↓
                                                        Compacting → (memory flush LLM call)
                                                              ↓
                                                        (summarization LLM call)
                                                              ↓
                                                        (persist SessionCompacted + snapshot)
                                                              ↓
                                                        Ready (or drain buffer)
```

During `Compacting`:
- Buffer incoming `SendUserMessage` commands (same as `Processing`)
- ACK them immediately so the adapter isn't blocked
- After compaction completes, drain buffer into a single batched follow-up turn

### Recommendation for Netclaw

Add `Compacting` as a third behavior state. It reuses the same buffering
pattern as `Processing`. The compaction sequence is:

1. Fire memory extraction LLM call (structured prompt)
2. On response: persist extracted memories externally
3. Fire summarization LLM call (structured compaction prompt)
4. On response: persist `SessionCompacted` event + take snapshot
5. Drain buffer or return to `Ready`

---

## 6. Sub-Agent Isolation for Long-Running Tasks

### The Pattern

Anthropic's context engineering guide recommends sub-agent architecture as the
most scalable pattern for truly long-running tasks. Each sub-agent gets its own
clean context window and returns only a condensed result (1-2k tokens) to the
parent.

This maps naturally to Akka's actor model:
- Parent session actor spawns child task actors
- Each child gets its own `SessionState` (or even its own `IChatClient`)
- Child returns a structured result message
- Parent incorporates the result summary into its own context

### Recommendation for Netclaw

Not MVP, but the architecture should not preclude it. The current design where
`SessionState` is decoupled from the actor already supports this — a child
actor could operate on its own `SessionState` instance without sharing the
parent's context.

---

## 7. Summary of Recommendations

| Area | Recommendation | Priority |
|------|---------------|----------|
| Compaction trigger | Token-count from `UsageDetails.InputTokenCount` | MVP |
| Tiered clearing | Clear old tool results before summarizing | MVP |
| Structured summarization | Domain-specific section headings, not generic | MVP |
| System prompt preservation | Already implemented, maintain invariant | Done |
| Compaction model | Optional `CompactionModelId` in config | Post-MVP |
| Tool pair integrity | Paired storage with shared `CallId` | MVP (with tool exec) |
| Pre-compaction memory flush | Structured extraction before compaction | MVP |
| Usage transparency | Enrich `UsageOutput` with context window metadata | MVP |
| Compacting behavior state | Third behavior state with buffering | MVP |
| Sub-agent isolation | Architecture supports it, defer implementation | Post-MVP |

---

## Sources

### Framework Documentation
- [OpenAI Agents SDK - Sessions](https://openai.github.io/openai-agents-python/sessions/)
- [OpenAI Compaction Guide](https://developers.openai.com/api/docs/guides/compaction/)
- [LangChain ConversationSummaryBufferMemory](https://python.langchain.com/api_reference/langchain/memory/langchain.memory.summary_buffer.ConversationSummaryBufferMemory.html)
- [Semantic Kernel - Managing Chat History](https://devblogs.microsoft.com/semantic-kernel/managing-chat-history-for-large-language-models-llms/)
- [Semantic Kernel - ChatHistoryTruncationReducer](https://learn.microsoft.com/en-us/dotnet/api/microsoft.semantickernel.chatcompletion.chathistorytruncationreducer)
- [AutoGen 0.2 - Transform Messages](https://microsoft.github.io/autogen/0.2/docs/topics/handling_long_contexts/intro_to_transform_messages/)
- [Anthropic - Context Editing](https://platform.claude.com/docs/en/build-with-claude/context-editing)
- [Anthropic - Context Windows](https://platform.claude.com/docs/en/build-with-claude/context-windows)
- [Anthropic - Effective Context Engineering](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents)
- [Google ADK - Context Compaction](https://google.github.io/adk-docs/context/compaction/)
- [LlamaIndex - Chat Summary Memory Buffer](https://developers.llamaindex.ai/python/examples/agent/memory/summary_memory_buffer/)
- [CrewAI - Memory](https://docs.crewai.com/en/concepts/memory)
- [Haystack - Memory for Conversational Agents](https://haystack.deepset.ai/blog/memory-conversational-agents)

### Independent Evaluations
- [JetBrains Research - Efficient Context Management (NeurIPS 2025)](https://blog.jetbrains.com/research/2025/12/efficient-context-management/)
- [Factory.ai - Evaluating Context Compression](https://factory.ai/news/evaluating-compression)
- [Phil Schmid - Context Engineering Part 2](https://www.philschmid.de/context-engineering-part-2)

### Additional References
- [Pinecone - LangChain Conversational Memory](https://www.pinecone.io/learn/series/langchain/langchain-conversational-memory/)
- [OpenAI Cookbook - Session Memory](https://cookbook.openai.com/examples/agents_sdk/session_memory)
- [Google ADK - 2-Minute Context Compaction](https://medium.com/google-cloud/2-minute-adk-context-compaction-in-a-snap-470da15c30f4)
