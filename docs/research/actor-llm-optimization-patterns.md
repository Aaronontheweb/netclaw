# Actor-Based LLM Optimization Patterns

Date: 2026-02-21
Context: Design research during session actor implementation (Tasks 1.2, 1.4, 1.6)

Patterns identified during implementation that are deferred from MVP but should
inform future work. Cross-referenced from the context management research and
direct implementation experience.

---

## 1. Prompt Cache Optimization in Actor Architecture

### Provider-Side Prefix Caching

Anthropic and OpenAI both cache prompt prefixes server-side. If the first N
tokens of the message array are identical to a recent call, those tokens are
read from cache (cheaper, faster). Anthropic's cache has a 5-minute TTL that
resets on each cache hit.

**Current state:** Netclaw's `SessionState.History` is append-only (until
compaction), so consecutive turns within a session naturally benefit from
prefix caching. The system prompt at slot 0 is always stable.

**What breaks caching:**
- Compaction replaces the entire history (except system prompt) — invalidates
  the provider's cached prefix
- Tool result clearing mutates messages in the middle of the history — changes
  the prefix from that point forward
- Actor recovery after passivation — if the provider cache has expired (>5min
  idle), the first post-recovery call pays full price

### Tier 1: Shared Prefix Warming

The system prompt is identical across all sessions. A lightweight
`CacheWarmerActor` could periodically send a minimal call to the provider with
just the system prompt, keeping it warm in the provider's cache.

- Implementation: timer actor, fires every ~4 minutes, sends a cheap
  completion call with only the system prompt
- Cost: nearly zero (minimal input/output tokens)
- Benefit: every first message in a new session gets a cache hit on the
  system prompt portion
- Dependency: requires provider abstraction (Task 1.8) to be in place

### Tier 2: Cache-Aware Compaction

Design the compaction summary to become the *new stable prefix* after
compaction. Current implementation already does this:
`[system prompt, summary]` → subsequent turns append to this stable base.

Potential enhancement: add Anthropic cache control breakpoints to the message
array, explicitly marking the system prompt and compaction summary as cache
boundaries. This tells the provider "cache everything up to here."

### Tier 3: IChatClient Decorator Chain

Wrap the real `IChatClient` in a decorator pipeline:

```
CachingChatClient → RetryingChatClient → RateLimitingChatClient → ProviderChatClient
```

The caching decorator could:
- Add provider-specific cache control headers/breakpoints
- Track cache hit/miss rates via `UsageDetails.CachedInputTokenCount`
- Optionally do semantic response caching for scheduled tasks (same prompt
  within a TTL = cached response)

The actor doesn't change — it calls `_chatClient.GetResponseAsync()` and the
decorator handles cache optimization transparently.

**Where this fits:** Task 1.8 (Provider abstraction). The decorator pattern
is how Semantic Kernel (filters) and LangChain (middleware) both handle this.

### Monitoring

`UsageOutput` already passes through `CachedInputTokens` from provider
responses. Subscribers can observe cache efficiency without actor changes.
A future ops console could display cache hit rates per session and globally.

---

## 2. Max Tool Iterations (Safety Circuit Breaker)

### The Problem

The agentic tool loop in `LlmSessionActor` has no iteration cap. If the LLM
keeps requesting tools indefinitely (hallucinated tool names, recursive tool
chains, confused models), the loop runs forever, consuming tokens and time.

### SDK Precedent

| SDK | Config | Default |
|-----|--------|---------|
| OpenAI Agents SDK | `max_turns` | 10 |
| Anthropic tool_runner | iteration limit | configurable |
| LangChain AgentExecutor | `max_iterations` | 15 |
| AutoGen | `max_consecutive_auto_reply` | configurable |

### Recommendation

Add `MaxToolIterationsPerTurn` to `SessionConfig` (default: 10). Track a
counter in the Processing state that increments on each `ToolExecutionCompleted`.
When the limit is reached, force a text response by calling the LLM without
tools in `ChatOptions`.

Priority: **high** — this is a safety concern, not just an optimization.

---

## 3. Parallel Tool Execution

### Current State

`ExecuteToolsAsync` runs tools sequentially via `foreach`. When the LLM
requests multiple independent tool calls in one response, they execute
one at a time.

### Recommendation

Replace sequential execution with `Task.WhenAll` for independent tool calls.
Tools are already independent (each has its own `FunctionCallContent` with
separate `CallId`). The `FakeToolExecutor` in tests already supports this.

```csharp
var tasks = toolCalls.Select(tc => ExecuteSingleToolAsync(executor, tc, ...));
var results = await Task.WhenAll(tasks);
```

Priority: **medium** — easy win, especially for I/O-bound tools (web search,
web fetch, GitHub CLI). Matters most when the LLM requests 3+ tool calls
simultaneously.

---

## 4. Streaming Responses

### Current State

`LlmSessionActor.FireLlmCall()` uses `GetResponseAsync` (non-streaming).
`FakeChatClient.GetStreamingResponseAsync` throws `NotSupportedException`.

### Design Question

Streaming can be implemented at two levels:
1. **Actor level:** Actor receives `ChatResponseUpdate` chunks and forwards
   them to subscribers as `StreamingTextChunk` outputs
2. **Adapter level:** Adapter subscribes to the session and handles streaming
   at its transport layer (Slack API supports updating messages in place)

Actor-level streaming is more complex (new behavior state, partial message
assembly, error handling mid-stream) but gives all adapters streaming for
free.

### Recommendation

Defer to Task 1.14 (TUI chat adapter) design. The TUI will need streaming
for acceptable UX. Design decision: whether `LlmSessionActor` streams
internally or whether a `StreamingSessionAdapter` wraps non-streaming calls.

Priority: **medium** — required before TUI adapter, not needed for core
actor logic.

---

## 5. Retry with Exponential Backoff

### Current State

`LlmCallFailed` handler gives up immediately and tells the user to try again.
No retry logic for transient errors (rate limits, timeouts, 5xx).

### Recommendation

Two approaches, not mutually exclusive:

1. **IChatClient decorator** (preferred): A `RetryingChatClient` decorator
   that retries on transient HTTP errors with exponential backoff. Transparent
   to the actor. Uses Polly or manual retry logic.

2. **Actor-level retry**: The actor handles `LlmCallFailed`, checks if the
   error is transient (rate limit, timeout), and re-fires `FireLlmCall()`
   with a scheduled delay via `Context.System.Scheduler`.

The decorator approach is cleaner and reusable across all actors. The
actor-level approach gives more control (e.g., different retry policies for
user-initiated vs. scheduled tasks).

**Critical for scheduled tasks:** A scheduled task that fails due to a
transient rate limit shouldn't require human intervention. The retry policy
should be configurable per session or per adapter type.

Priority: **medium-high** — important for production reliability, especially
for scheduled tasks (Task 1.12).

---

## 6. Sub-Agent Isolation

### Architecture Support

Already supported by the current design:
- `SessionState` is decoupled from the actor
- A parent session actor could spawn child task actors with their own
  `SessionState` and `IChatClient`
- Child returns a structured result message (1-2k tokens)
- Parent incorporates the condensed result into its own context

### Recommended Pattern

```
ParentSessionActor (main conversation)
  ├── ChildTaskActor (research sub-task, own context window)
  ├── ChildTaskActor (code review sub-task, own context window)
  └── ChildTaskActor (scheduled analysis, own context window)
```

Each child gets its own clean context window. The parent doesn't pay the
token cost of the child's full conversation — only the condensed result.

Maps to Anthropic's context engineering guide recommendation for truly
long-running tasks.

Priority: **low** — Phase 3 (Delegated Coding) is the natural entry point.

---

## Summary

| Pattern | Priority | Depends On | Phase |
|---------|----------|------------|-------|
| Max tool iterations | High | None (SessionConfig change) | 1 |
| Parallel tool execution | Medium | None (ExecuteToolsAsync change) | 1 |
| Retry with backoff | Medium-High | Task 1.8 (provider abstraction) | 1 |
| Streaming responses | Medium | Task 1.14 (TUI adapter) | 1 |
| Prompt cache warming | Low-Medium | Task 1.8 (provider abstraction) | 2+ |
| Cache-aware compaction | Low | Task 1.8 | 2+ |
| IChatClient decorator chain | Medium | Task 1.8 | 1 |
| Sub-agent isolation | Low | Phase 3 architecture | 3 |
