## 1. Shared bounded-output reader (foundation, no behavior change)

- [ ] 1.1 Extract `BoundedDrainAsync` from `ShellTool` into a reusable
      `BoundedOutputReader` (Netclaw.Actors/Tools), keeping the #1293 ring core
      (pooled buffer, `ValueTask` reads, block-copy tail ring)
- [ ] 1.2 Expose `DrainToWindowAsync(TextReader, int budget, ct) → (string Text,
      bool Truncated)` (head+tail, in-memory only)
- [ ] 1.3 Expose `DrainCaptureAsync(TextReader, int captureMax, int inlineBudget,
      ct) → (string Captured, string Inline, bool Truncated)`; drain past
      `captureMax` to avoid child deadlock
- [ ] 1.4 Move/adapt the existing `BoundedDrainAsync` unit tests (window, exact-at-cap,
      wraparound/start-advance, disabled cap) onto `BoundedOutputReader`
- [ ] 1.5 Repoint `ShellTool` at `BoundedOutputReader.DrainToWindowAsync` with no
      behavior change yet; confirm all shell tests stay green

## 2. Tool-call id and inline budget plumbed to the capture layer

- [ ] 2.1 Add `ToolCallId` to `ToolExecutionContext` (additive, nullable)
- [ ] 2.2 Set `ToolCallId` in `SessionToolExecutionPipeline` at context-build time
      from the call's `ToolCallMeta`
- [ ] 2.3 Make the inline budget `N` (`MaxInlineToolResultChars`) reachable at the
      capture layer (via `ToolExecutionContext`), so tools bound to the same `N`
      the pipeline enforces
- [ ] 2.4 Tests: context carries the call id; two concurrent calls get distinct ids

## 3. Spill writer + steering message

- [ ] 3.1 Add a spill writer that creates `{sessionDirectory}/tool-calls/` and
      writes `{toolCallId}.log`, redacting the bounded capture buffer with
      `SecretOutputRedactor` in a single pass before write (redact-on-write)
- [ ] 3.2 Add a steering-message builder (path + "read ranges with file_read
      offset/limit or grep instead of re-running"); include a "capture ceiling
      exceeded" note when `MaxOutputChars` was hit
- [ ] 3.3 Tests: spill file written and redacted; path + steer present in the
      inline result; ceiling-exceeded note when over `MaxOutputChars`

## 4. shell_execute adopts capture + spill

- [ ] 4.1 Switch `ShellTool` to `DrainCaptureAsync` with one shared budget across
      stdout+stderr (resolves the per-stream doubling), spilling + steering when
      combined output exceeds `N`
- [ ] 4.2 Update `ShellToolTests` for the new shape (head+tail inline, spill path,
      single combined budget); keep the Windows-deterministic `echo` command
- [ ] 4.3 Verify the `MaxOutputChars` capture ceiling drains-past behavior under a
      flood (no deadlock, bounded memory)

## 5. background_job bounded capture (closes #1300)

- [ ] 5.1 Rework `BackgroundJobExecutionActor` to stream stdout/stderr to the job
      log via the shared capture (bounded memory) and retain only a tail for the
      completion message — stop materializing the full output string
- [ ] 5.2 Apply redact-on-write to the job log
- [ ] 5.3 Tests: large-output job stays within the memory bound; completion
      carries the bounded tail + log path

## 6. file_read bounded reads + redaction (closes #1301)

- [ ] 6.1 Bound the default `file_read` path: return a head+tail sample within `N`
      for an over-budget file instead of `ReadAllTextAsync`; no separate spill
- [ ] 6.2 Add the over-budget steer (use `offset`/`limit` or `grep`)
- [ ] 6.3 Run returned content through `SecretOutputRedactor` (redact-on-read);
      keep the bounded `ReadLinesAsync` offset/limit path
- [ ] 6.4 Tests: large file returns bounded sample without full materialization;
      secret in a read file is redacted

## 7. Config + pipeline unification

- [ ] 7.1 Lower `SessionTuning.MaxInlineToolResultChars` default 12000 → 2000
- [ ] 7.2 Repurpose `ToolConfig.MaxOutputChars` as the capture ceiling and raise
      its default to a comfortable spill size
- [ ] 7.3 Make `SessionToolExecutionPipeline.ClampToolResult` head+tail (was
      head-only); it remains the safety net for non-shared-reader results
- [ ] 7.4 Update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`
      for both default changes (Config Schema Sync Rule)

## 8. Quality gates, docs, eval, and OpenSpec close-out

- [ ] 8.1 `dotnet slopwatch analyze` clean; `./scripts/Add-FileHeaders.ps1 -Verify`
- [ ] 8.2 Update the `netclaw-operations` system skill (tool output now spills to a
      session-dir file; steer to ranged reads/grep) and bump its `metadata.version`
- [ ] 8.3 Add/adjust eval cases for the changed tool-output behavior (truncation
      shape, spill-path presence, file_read range steer)
- [ ] 8.4 Benchmark the shared reader's capture path (extend the existing
      `Netclaw.Benchmarks` harness) to confirm O(ceiling) capture allocation
- [ ] 8.5 `openspec sync` / `openspec verify` the change; archive on completion
