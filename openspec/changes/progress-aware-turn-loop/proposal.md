## Why

The turn loop's `MaxToolCallsPerTurn` budget (default 30) is a per-turn count of
individual tool calls that only ever increases; at 100% the loop strips tools,
forces a summary, and — if the model still requests a tool — fails the turn,
discarding all completed work (issue #1098). The count is a volume proxy that
never inspects the work: it cannot tell a healthy multi-step turn from a turn
spinning on a failing command, it is blind to parallelism (8 parallel reads cost
the same budget as 8 serial reads), and its exhaustion path throws away minutes
of real progress. Scheduled reminders that legitimately do heavy multi-step work
(e.g. `vllm-sglang-mtp-amd-watch` — subagents, shell commands, file reads,
summaries) hit this and fail intermittently. This change governs the loop by
*progress* instead of *volume*.

## What Changes

- Add a **progress governor** as the primary loop control: each completed tool
  iteration is classified productive or unproductive (new non-error results vs.
  all-error/all-duplicate), and a run of consecutive unproductive iterations
  wraps the turn up early — within a few iterations, not at 30.
- Add a **scale advisory**: a continuous, model-facing status line (iteration
  count, approximate cumulative tokens, a standing instruction to checkpoint
  findings and prefer subagent decomposition) replacing the one-shot 75% nudge.
  Advisory only — never ends a turn.
- Add a **graceful handoff contract**: every turn-ending path delivers whatever
  work was completed and, for an incomplete task, states what remains. A
  reminder-sourced incomplete turn writes a progress note to memory so the next
  scheduled fire resumes.
- **BREAKING** — remove the `force_no_tools_violation → FailCurrentTurn` path: a
  wrap-up response that still emits tool calls is recovered (closed-tool
  `tool_result` synthesized, re-prompted once), never failed for resource
  reasons.
- **BREAKING** (config) — replace `SessionConfig.MaxToolCallsPerTurn` (default
  30) with `MaxToolIterationsPerTurn` (default 60): a high, iteration-based
  safety fuse that routes into the same handoff. Iteration-based so a model
  queued on a busy self-hosted backend is never penalized.

## Capabilities

### New Capabilities

- `turn-loop-governance`: how the session turn loop decides to continue,
  wrap up, or hand off — the progress governor, scale advisory, graceful
  handoff contract, and iteration-fuse safety limit. Existing turn-loop
  behavior (the `MaxToolCallsPerTurn` budget, the force-no-tools summary, the
  exhaustion failure) is currently unspecified; this capability specifies the
  replacement.

### Modified Capabilities

- `session-config-decomposition`: the Session config surface changes —
  `MaxToolCallsPerTurn` is removed and `MaxToolIterationsPerTurn` is added,
  updating the default-`SessionConfig` and JSON-schema-validation requirements
  that name the old property.

## Impact

- **Source PRDs**: PRD-001 FR-002 (Turn Processing), FR-011 (Tool Access);
  PRD-008 (Scheduling and Periodic Tasks) — the failing use case is a reminder.
- **Code**: `TurnStateTracker` (progress classification, reframed
  `ToolBudgetStatus`), `LlmSessionActor` (`HandleToolExecutionCompleted`,
  removal of the force-no-tools failure path, wrap-up + closed-tool recovery,
  advisory injection in `FireLlmCall`), `SessionConfig` / `SessionTuning`
  (config rename, `UnproductiveIterationLimit`).
- **Config / ops**: `netclaw-config.v1.schema.json` Session section (remove
  `MaxToolCallsPerTurn`, add `MaxToolIterationsPerTurn` with a `default`);
  existing configs with `MaxToolCallsPerTurn` are reconciled by `doctor --fix`.
- **Agent guidance**: `netclaw-operations` system skill — document loop
  self-governance and checkpointing partial work.
- **No persistence/serialization changes**; turn phases and the `TurnRecorded`
  event are unchanged.
