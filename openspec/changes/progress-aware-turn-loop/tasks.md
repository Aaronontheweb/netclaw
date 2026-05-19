## 1. Configuration and schema

- [ ] 1.1 Replace `MaxToolCallsPerTurn` with `MaxToolIterationsPerTurn` (default 60) in `SessionConfig` and `RawSessionConfig`; update `BindFromConfiguration`.
- [ ] 1.2 Add `UnproductiveIterationLimit` (default 3) to `SessionTuning`; wire it into `BindTuning`.
- [ ] 1.3 Update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`: remove `MaxToolCallsPerTurn`, add `MaxToolIterationsPerTurn` with `"default": 60`, add `UnproductiveIterationLimit` to the nested `Tuning` object.
- [ ] 1.4 Update XML doc comments on the new config members to describe iteration-based governance (replace the old call-count/75%/100% wording).

## 2. Progress governor (`TurnStateTracker`)

- [ ] 2.1 Reframe `ToolBudgetStatus` to `Ok` and `WrapUp(WrapUpReason)`; add `WrapUpReason` (`NoProgress`, `IterationFuse`); remove `NudgeNeeded` and `Exhausted`.
- [ ] 2.2 Add productive/unproductive iteration classification: extend `RecordToolCompletion` to receive per-result error status and tool-call fingerprints, and classify the completed iteration.
- [ ] 2.3 Add `_consecutiveUnproductiveIterations` — increment on an unproductive iteration, reset to zero on a productive one; return `WrapUp(NoProgress)` when it reaches `UnproductiveIterationLimit`.
- [ ] 2.4 Change the fuse check to `ToolIterationCount >= MaxToolIterationsPerTurn` returning `WrapUp(IterationFuse)`.
- [ ] 2.5 Fold `CheckForDuplicates` into the classification path; reset the new counters in `ResetForNewTurn` and `ResetToolCounters`.
- [ ] 2.6 Keep `ToolCallCount` updated as telemetry only (no longer a control input).

## 3. Wrap-up handoff (`LlmSessionActor`)

- [ ] 3.1 In `HandleToolExecutionCompleted`, consume `Ok`/`WrapUp`; on `WrapUp` inject the wrap-up instruction and fire `FireLlmCall(forceNoTools: true)`; emit a structured `turn_wrapup reason=...` log event.
- [ ] 3.2 Remove the `force_no_tools_violation → FailCurrentTurn` path in `HandleLlmResponseReceived`.
- [ ] 3.3 Implement closed-tool recovery: when a `forceNoTools` response still contains tool calls, synthesize a closed-tool `tool_result` for every `tool_use` block and re-prompt once with `forceNoTools`; bound this to a single attempt.
- [ ] 3.4 Implement best-available partial delivery when the bounded re-prompt yields no usable reply text; ensure no `FailCurrentTurn` is reachable for resource reasons (retain it only for genuine provider errors).
- [ ] 3.5 Confirm the wrap-up reply is persisted as `TurnRecorded.AssistantReply` so an incomplete reminder turn resumes organically on the next fire.

## 4. Scale advisory

- [ ] 4.1 Build the advisory text (current iteration count, approximate cumulative tokens, instruction to checkpoint findings and prefer subagent decomposition).
- [ ] 4.2 Inject the advisory as ephemeral, non-persisted system context in `FireLlmCall`, refreshed each call and shown once a turn passes a low iteration threshold; remove the one-shot 75% budget nudge.
- [ ] 4.3 Verify the advisory is excluded from `_state.History`, compaction input, and the persisted `TurnRecorded` event.

## 5. Agent guidance

- [ ] 5.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` to document loop self-governance, the scale advisory, and checkpointing partial work for incomplete tasks; bump `metadata.version`.

## 6. Tests and verification

- [ ] 6.1 `TurnStateTracker` unit tests: productive vs unproductive classification (new results, all-errors, all-duplicates); `WrapUp(NoProgress)` after K consecutive unproductive iterations; counter reset on a productive iteration; `WrapUp(IterationFuse)` at the fuse; a parallel batch counts as one iteration.
- [ ] 6.2 Session-actor test: a turn reaching the iteration fuse delivers partial work and never calls `FailCurrentTurn`.
- [ ] 6.3 Session-actor test: a `forceNoTools` response containing tool calls is recovered (closed-tool results synthesized, single re-prompt) and not failed; after the bounded re-prompt a partial result is delivered.
- [ ] 6.4 Session-actor test: a long healthy multi-step turn (many productive iterations) runs past the former 30-call budget and completes normally.
- [ ] 6.5 Config tests: default `MaxToolIterationsPerTurn` is 60 and `UnproductiveIterationLimit` is 3; schema validation accepts `MaxToolIterationsPerTurn` and rejects a stale `MaxToolCallsPerTurn` as an unknown property.
- [ ] 6.6 Add an eval regression case for a long multi-step turn (loop governance is tool/behavior-affecting per the Eval Suite rule).
- [ ] 6.7 Run `dotnet build`, the session-actor test suite, `dotnet slopwatch analyze`, `./scripts/Add-FileHeaders.ps1 -Verify`, and `./evals/run-evals.sh`.
- [ ] 6.8 Run `/opsx-verify` to confirm the implementation matches the change artifacts before sync/archive.
