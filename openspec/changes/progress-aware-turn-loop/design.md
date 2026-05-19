## Context

The session turn loop (`LlmSessionActor` + `TurnStateTracker`) iterates between
LLM calls and tool-batch execution until the model produces a text-only reply.
Today the loop is bounded by `SessionConfig.MaxToolCallsPerTurn` (30): each
completed batch adds its result count to `TurnStateTracker.ToolCallCount`; at 75%
a one-shot nudge is injected; at 100% `ToolBudgetStatus.Exhausted` strips tools
(`FireLlmCall(forceNoTools: true)`) and demands a summary. If the model emits a
tool call anyway, `HandleLlmResponseReceived` calls `FailCurrentTurn`, discarding
the whole turn (see proposal / issue #1098).

`TurnStateTracker` is a plain per-actor object holding transient per-turn state;
it is never persisted and is reset by `ResetForNewTurn()` at the start of each
turn. The actor runs in the `Processing` phase for the whole loop. Token usage is
already tracked per LLM call (`_lastInputTokenCount`, `_sessionMetrics`).

## Goals / Non-Goals

**Goals:**

- Govern the loop by *progress*, not tool-call volume — a healthy multi-step
  turn runs as long as it keeps advancing.
- Catch a non-advancing (spinning) turn within a few iterations, cheaply.
- Guarantee that no turn-ending path discards completed work; remove
  resource-driven `FailCurrentTurn`.
- Surface running cost to the model so it self-rations.
- Keep the safety limit queue-immune (a model queued on a busy self-hosted
  backend must never be penalized).

**Non-Goals:**

- No wall-clock turn deadline (would penalize slow self-hosted prefill; Netclaw's
  timeouts are deliberately inactivity-based).
- No new persisted state, no journal/snapshot schema change, no new turn phase.
- No formal "continuation turn" object — resumption of an incomplete reminder is
  organic, via the persisted turn reply (see Decision 4).
- No semantic/embedding-based progress detection — out of scope for MVP.

## Decisions

### Decision 1: Progress governor as the primary loop control

`TurnStateTracker` classifies each completed tool iteration as **productive** or
**unproductive**. An iteration is *productive* when at least one result in the
batch is (a) not an error **and** (b) not a duplicate of a tool-call fingerprint
(tool name + arguments JSON) already seen this turn; otherwise *unproductive*.
The tracker keeps `_consecutiveUnproductiveIterations`, incremented on an
unproductive iteration and reset to 0 on a productive one. When it reaches
`UnproductiveIterationLimit` (default 3), `RecordToolCompletion` returns
`WrapUp(NoProgress)`.

*Why:* the failure the count was meant to catch is *spinning*, and spinning has a
signature — repeated errors and repeated identical calls. Detecting that directly
fires at iteration ~4 instead of ~30 and never penalizes a healthy long turn. The
existing `CheckForDuplicates` fingerprint map (`_toolCallCounts`) is reused as one
input rather than a separate nudge.

*Alternatives:* keep tool-call counting (rejected — the premise of the change);
information-gain via output diffing or embedding similarity (rejected for MVP —
heavy and brittle; the fuse covers the residual false-negative).

### Decision 2: Reframe `ToolBudgetStatus` to `Ok` / `WrapUp(reason)`

The `Ok` / `NudgeNeeded` / `Exhausted` union collapses to `Ok` and
`WrapUp(WrapUpReason)`. `WrapUpReason` is `NoProgress` or `IterationFuse`.
`NudgeNeeded` disappears — the 75% one-shot nudge is replaced by the continuous
advisory (Decision 3). `RecordToolCompletion` no longer takes a call-count cap; it
takes the iteration fuse and the batch result details needed for classification.

### Decision 3: Continuous, model-facing scale advisory

The one-shot 75% nudge is replaced by a short advisory line recomputed every LLM
call and injected as ephemeral system context in `FireLlmCall` — **not** persisted
to `_state.History`. It reports the iteration count, approximate cumulative
tokens, and a standing instruction to checkpoint findings and prefer delegating
independent sub-tasks to subagents (which receive their own budget). It is shown
once the turn crosses a low iteration threshold and refreshed thereafter.

*Why ephemeral:* the advisory is a live gauge derived from current counters.
Persisting a nudge per iteration would pollute history and compaction; recomputing
it each call keeps exactly one current copy in context. It never ends a turn —
it is information, not a tripwire.

### Decision 4: Graceful handoff contract — no resource-driven failure

On `WrapUp`, the actor injects a wrap-up instruction ("produce your final answer
now; state what you completed and, if the task is incomplete, exactly what remains
so it can be resumed") and fires `FireLlmCall(forceNoTools: true)`.

The `force_no_tools_violation → FailCurrentTurn` path is **removed**. If a
`forceNoTools` response still contains tool calls, the actor synthesizes a
closed-tool `tool_result` for every `tool_use` block (text: tool execution is
closed for this turn) — preserving the Anthropic contract that every `tool_use`
is answered — and re-prompts once more with `forceNoTools`. That re-prompt is
bounded to a single attempt. If it still yields no usable text, the actor
delivers the best available partial: accumulated assistant text, or, failing
that, a harness-built summary referencing the completed tool results. A turn that
did work always delivers work.

*Resumption:* for a reminder-sourced incomplete turn, the final reply itself
states what is done and what remains. That reply is persisted as
`TurnRecorded.AssistantReply` and is visible to the next scheduled fire through
ordinary thread history and memory recall. No tool-enabled wrap-up stage and no
explicit memory write are needed — which keeps the wrap-up free of runaway risk.

`FailCurrentTurn` is retained only for genuine errors (provider failure during the
wrap-up call, etc.) — never for resource exhaustion.

### Decision 5: Iteration-based safety fuse

`SessionConfig.MaxToolCallsPerTurn` is replaced by `MaxToolIterationsPerTurn`
(default 60), counting `ToolIterationCount` (LLM↔tool round-trips). One LLM
response with N parallel `tool_use` blocks is one iteration. Reaching the fuse
returns `WrapUp(IterationFuse)` into the same handoff.

*Why iterations:* queue-immune (a queued model has not completed an iteration),
trivially computed, already tracked, and it rewards parallelism (the round-trip
is the unit of real cost). 60 is high enough that a healthy turn never reaches it
— the progress governor catches sick turns far earlier. Tokens are surfaced
advisory-only (Decision 3): a token *cliff* would still be a cliff, and one large
legitimate response resembles a runaway. Wall-clock is excluded (Non-Goals).

### Decision 6: Config migration

`MaxToolIterationsPerTurn` is added to `SessionConfig`, `RawSessionConfig`, and
`BindFromConfiguration`; `MaxToolCallsPerTurn` is removed. `UnproductiveIterationLimit`
is added to `SessionTuning` (internal, default 3). Per the CLAUDE.md schema rules,
`netclaw-config.v1.schema.json` removes `MaxToolCallsPerTurn` and adds
`MaxToolIterationsPerTurn` **with a `"default": 60`** so `SchemaFixResolver` can
insert it; the stale `MaxToolCallsPerTurn` in an existing config becomes an
unknown property that `additionalProperties: false` rejects and `doctor --fix`
removes.

## Risks / Trade-offs

- Progress heuristic false-negative — the model makes technically-new but useless
  calls (slightly varied args, circling) → the iteration fuse (60) bounds the
  residue; duplicate detection still contributes on near-identical calls.
- Progress heuristic false-positive — a turn legitimately retrying a flaky tool
  looks unproductive → 3 *consecutive* unproductive iterations is conservative, a
  single productive iteration resets the counter, and a false wrap-up still
  *delivers* completed work rather than failing. Tunable via `SessionTuning`.
- Removing the hard fail could mask a genuinely stuck turn → every wrap-up logs a
  structured `turn_wrapup reason=...` event; `ToolCallCount` and iteration counts
  are retained as telemetry.
- Config breaking change → documented; `doctor --fix` reconciles existing configs;
  schema `default` enables auto-insert.
- Advisory adds context tokens → one short line, recomputed not accumulated;
  strictly cheaper than today's persisted per-iteration nudges.

## Migration Plan

Single PR, no data migration. On deploy the daemon honors
`MaxToolIterationsPerTurn`; a stale `MaxToolCallsPerTurn` in an existing
`netclaw.json` is flagged by `ConfigSchemaDoctorCheck` at startup and removed by
`netclaw doctor --fix`. Rollback is reverting the PR — a config that was
`doctor --fix`'d simply lacks the old key, and the reverted code's default (30)
applies, so rollback is safe.

`TurnStateTracker` is transient and unpersisted: on a mid-turn crash and recovery
the tracker resets and the turn re-drives from journaled tool-batch state with a
fresh progress budget. This is acceptable — recovery is rare and the fuse still
bounds the recovered turn.

## Open Questions

- Default values for `MaxToolIterationsPerTurn` (60) and `UnproductiveIterationLimit`
  (3) — to be sanity-checked against real reminder run telemetry before release.
- The low iteration threshold at which the scale advisory first appears — proposed
  to start partway through the budget rather than at iteration 1, to avoid noise
  on short turns; exact value deferred to implementation.
