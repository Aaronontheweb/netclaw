## Why

Large tool output is bounded inconsistently across the codebase, and the two
truncation stages that do exist disagree. `shell_execute` now bounds its pipe
reads to a head+tail window (`BoundedDrainAsync`, #1293), but the session
pipeline then re-clamps every tool result to `MaxInlineToolResultChars`
**head-only** — so the tail that `BoundedDrainAsync` worked to preserve (errors,
exit status) is discarded before the model sees it, and `stderr` can be dropped
entirely. Meanwhile two sibling paths still materialize unbounded output in
memory and can OOM a memory-limited daemon: `background_job` output capture
(#1300) and the default `file_read` path (#1301); `file_read` also returns file
contents with no secret redaction at all.

The fix is to make output bounding a single, shared, deliberate mechanism:
one budget, one policy (head + tail), and — when output exceeds the budget —
spill the full output to a session-scoped file and hand the model the path with
a hint to read ranges or `grep` instead of re-running. This is the pattern both
Claude Code and opencode ship, and it is the pattern `background-job-execution`
already half-specifies (tail + output-file path).

No dedicated PRD exists; this originates from the #1293 production OOM incident
and its review (#1300, #1301). It should be linked from a PRD if one is opened.

## What Changes

- Introduce one bounded-output mechanism shared by `shell_execute`,
  `background_job`, and `file_read`: stream the source in bounded memory,
  retain an N/2-head + N/2-tail window, and report whether truncation occurred.
- **BREAKING (behavioral):** lower the default `MaxInlineToolResultChars`
  from 12000 to **2000**, and make it the single budget `N` for inline tool
  output. All inline tool results shrink accordingly. (Already configurable;
  no schema change beyond the default.)
- **BREAKING (behavioral):** replace head-only `ClampToolResult` truncation
  with the shared head+tail policy, so the inline result keeps both ends.
- When a tool's output exceeds `N`, spill the full (redacted) output to
  `sessionDir/tool-calls/{toolCallId}.log` and append a steering message with
  the path, directing the model to `file_read` (offset/limit) or `grep`.
- Plumb `ToolCallId` into `ToolExecutionContext` so the spill file can be named
  per call.
- Repurpose `ToolConfig.MaxOutputChars` as the **capture ceiling** — the bounded
  buffer that becomes the (redacted) spill body — and raise its default from
  32000 to a comfortable spill size. It no longer doubles as the inline budget;
  `N` owns that.
- `file_read`: bound the default path (no more `ReadAllTextAsync` of the whole
  file); for an over-budget file, return a head+tail sample plus a steer to
  offset/limit/`grep` rather than materializing it (closes #1301 OOM).
- `background_job`: stream stdout/stderr to the on-disk log in bounded memory
  while retaining only a tail for the completion message (closes #1300 OOM).
- **Redaction, two modes:**
  - *redact-on-write* for files this system emits (shell/job spill logs),
    reusing the existing `SecretOutputRedactor` over a bounded capture buffer;
  - *redact-on-read* in `file_read` for files written by anything else
    (`file_read` does no redaction today — closes the #1301 cleartext gap).

### Out of scope (this change)

- Per-tool overrides of `N` (a follow-up; `MaxInlineToolResultChars` is global).
- Spill-file lifecycle/cleanup (tracked separately; the session-log cleanup
  issue owns retention/sweep).
- A byte-complete (unbounded) spill: capture is bounded by `MaxOutputChars`, so a
  multi-hundred-MB flood is captured head+tail, not in full. See design D5/D8.
- **Media egress (#1296)** — image/AV bytes sent *to* the model. It shares the
  "don't `ReadAllBytes` a huge thing" lesson but needs a different fix
  (downscale/streamed-encode/provider file APIs), so it stays a separate change;
  this one closes only the **text** tool-output paths (#1300, #1301).

## Capabilities

### New Capabilities

- `bounded-tool-output`: the cross-cutting contract for bounding any tool's
  external output — single budget `N`, head+tail retention, full-output spill to
  a session-scoped file with a model-facing steering hint, and the two-mode
  redaction rules (redact-on-write for emitted files, redact-on-read for foreign
  files). Owns the shared bounded-output reader and the spill-path convention.

### Modified Capabilities

- `netclaw-tools`: `shell_execute` and `file_read` adopt the shared bounded
  reader and the spill+steer behavior; the truncation requirement changes from a
  single head-only indicator to head+tail plus an output-file path; `file_read`
  gains bounded reads, reject/steer for over-budget files, and redact-on-read.
- `netclaw-session`: tool-result inlining unifies on the single budget `N`
  (`MaxInlineToolResultChars`, default 2000) with the head+tail policy;
  `ClampToolResult` aligns to (or defers to) the shared mechanism instead of an
  independent head-only clamp.
- `background-job-execution`: output capture SHALL bound memory — stream to the
  output log + retain a tail — rather than buffering the full output as a
  managed string before trimming.
- `tool-call-metadata`: the tool-call identifier is exposed to tools via
  `ToolExecutionContext` so emitted spill files can be named per call.

## Impact

- **Code:** new shared bounded-output reader (extracted from
  `ShellTool.BoundedDrainAsync`); `ShellTool`, `FileReadTool`,
  `BackgroundJobExecutionActor`, `SessionToolExecutionPipeline`
  (`ClampToolResult`), `ToolExecutionContext` (+`ToolCallId`); reuse of the
  existing `SecretOutputRedactor` (no new redaction abstraction).
- **Config:** `SessionTuning.MaxInlineToolResultChars` default 12000 → 2000.
- **Security:** closes the `file_read` cleartext-secret gap (redact-on-read);
  keeps emitted spill files redacted on disk (redact-on-write). Spill files live
  under the session directory and inherit its access scope.
- **Operational:** large tool outputs now produce a session-dir `.log` file the
  agent reads on demand; the model is steered toward ranged reads/`grep` instead
  of re-running expensive commands. Closes #1300 and #1301; resolves the #1293
  review's two-stage-truncation tension.
- **Evals/skills:** tool-output behavior changes — update `netclaw-operations`
  (and any eval cases asserting on tool-result truncation/format) accordingly.
