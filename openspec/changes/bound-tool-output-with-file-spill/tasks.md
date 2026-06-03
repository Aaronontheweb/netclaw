## 1. Shared bounded-output reader (foundation, no behavior change)

- [x] 1.1 Extract `BoundedDrainAsync` from `ShellTool` into a reusable
      `BoundedOutputReader` (Netclaw.Actors/Tools), keeping the #1293 ring core
      (pooled buffer, `ValueTask` reads, block-copy tail ring)
- [x] 1.2 Expose `DrainToWindowAsync(TextReader, int budget, ct) → (string Text,
      bool Truncated)` (head+tail, in-memory only)
- [x] 1.3 Expose `DrainCaptureAsync(TextReader, int captureMax, int inlineBudget,
      ct) → (string Captured, string Inline, bool CeilingExceeded)` over a shared
      core, plus a pure `Window(string, budget)` helper; drain past `captureMax`
- [x] 1.4 Move/adapt the `BoundedDrainAsync` unit tests onto `BoundedOutputReader`
      (`DrainToWindow_*`), add `Window` + `DrainCapture` coverage
- [x] 1.5 Repoint `ShellTool` and the benchmark at
      `BoundedOutputReader.DrainToWindowAsync` with no behavior change; 40 reader +
      shell tests green

## 2. Tool-call id and inline budget plumbed to the capture layer

- [x] 2.1 Add `ToolCallId` (typed value object, nullable) to `ToolExecutionContext`
- [x] 2.2 Set `ToolCallId` in `BuildToolExecutionContext` (per-call) from `tc.CallId`
- [x] 2.3 Add `MaxInlineToolResultChars` to `ToolExecutionContext` and thread `N`
      through `BuildToolExecutionContext` so tools bound to the same `N` the
      pipeline enforces
- [x] 2.4 Carry/distinctness is compiler-enforced (per-call context) + verified
      end-to-end by the task-4 spill test ({callId}.log); skip a trivial
      assignment test per the testing guidelines. (Sub-agent/direct-construction
      contexts default to null id + 0 budget — fallback handled in task 3/4.)

## 3. Spill writer + steering message

- [x] 3.1 Add `ToolOutputSpill.RenderAsync`: redact the bounded capture once,
      window the inline from the redacted text, and write
      `{sessionDirectory}/tool-calls/{toolCallId}.log` (call id sanitized against
      path traversal). Removed the redundant `DrainCaptureAsync` — the real flow
      is DrainToWindow → redact → Window → spill (inline must come from the
      *redacted* capture), so DrainToWindow + Window + the spill helper supersede it.
- [x] 3.2 Steering message (path + "read a slice with file_read offset/limit or
      grep instead of re-running") + a "capture ceiling exceeded" note
- [x] 3.3 Tests: under-budget verbatim; over-budget spill written + redacted-on-disk
      + steer; ceiling note; no-session degrade; path-traversal call id contained.
      15 reader+spill tests pass; slopwatch clean

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
