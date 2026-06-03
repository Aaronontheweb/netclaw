## Context

Three tool paths capture external output and feed it to the model:
`shell_execute` (process pipes), `background_job` (process pipes, long-running),
and `file_read` (a file on disk). Today they bound output inconsistently:

- `shell_execute` drains its pipes into a head+tail window capped at
  `ToolConfig.MaxOutputChars` (32000) via `BoundedDrainAsync` (#1293), in the
  tool.
- The session pipeline then re-clamps **every** tool result to
  `SessionTuning.MaxInlineToolResultChars` (12000), **head-only**, in
  `SessionToolExecutionPipeline.ClampToolResult`.
- `background_job` and the default `file_read` path do not bound memory at all —
  they materialize the full output/file as a managed string before trimming
  (#1300, #1301). `file_read` also performs no secret redaction.

The two truncation stages disagree on both budget (32000 vs 12000) and policy
(head+tail vs head-only), so the tail `shell_execute` preserves is discarded
before the model sees it, and the unbounded paths remain OOM risks. The
`background-job-execution` spec already describes the target shape (a retained
tail plus an on-disk output-file path), and both Claude Code and opencode ship
the same "bound inline, spill full to a file, steer the model to read ranges /
grep" pattern.

A key structural constraint drives the design: **the full output only exists
transiently inside the tool as it streams.** By the time output reaches the
pipeline's `ClampToolResult`, it is already a bounded string — the pipeline
cannot spill what it never saw. Therefore bounding-with-spill must happen at the
tool / capture layer, not the pipeline.

## Goals / Non-Goals

**Goals:**

- One inline budget `N` and one truncation policy (N/2 head + N/2 tail) for tool
  output the model sees inline.
- When output exceeds `N`, persist the full output to a session-scoped file and
  return its path with a steering hint (read ranges / `grep`, don't re-run).
- Bounded memory on every capture path — no path materializes arbitrarily large
  output as a managed string (closes #1300, #1301 OOM).
- Secret redaction on every model-facing path: redact-on-write for files this
  system emits, redact-on-read for foreign files (closes #1301 cleartext gap).
- A single shared bounded-output reader so the ring/window logic is reviewed and
  fixed once, not copy-pasted (the #1293 review's altitude finding).

**Non-Goals:**

- Per-tool overrides of `N` (follow-up; `N` is global via
  `MaxInlineToolResultChars`).
- Spill-file retention / cleanup (owned by the session-log-cleanup issue).
- Redacting pre-existing on-disk artifacts the agent did not read through a tool.
- Changing how MCP tool results are produced (they remain covered only by the
  pipeline safety-net clamp; see D7).

## Decisions

### D1 — One inline budget `N`, sourced from `MaxInlineToolResultChars`, plumbed to the capture layer

`N` is `SessionTuning.MaxInlineToolResultChars` (default lowered 12000 → 2000).
Because spill must happen where the full output exists (the tool), the tool needs
`N`. Plumb it onto `ToolExecutionContext` (the seam already carrying
session-scoped state), alongside `ToolCallId` (D4). The tool bounds its inline
result to `N` (head+tail) and spills the remainder; `ClampToolResult` then sees a
result already `≤ N` and is a no-op for shared-reader tools.

- *Alternative — bound only in the pipeline:* rejected; the pipeline cannot
  spill (no full output) and cannot apply head+tail before the tool has already
  decided what to keep.
- *Alternative — a new dedicated budget knob:* rejected; `MaxInlineToolResultChars`
  already means exactly "how much tool output goes inline," is already
  configurable, and a second knob would re-create the two-budget drift this
  change is removing.

### D2 — Spill happens in the capture layer; the pipeline clamp becomes a safety net

The tool (via the shared reader) owns: bound-to-`N`, tee-full-to-spill, build the
steering message. `ClampToolResult` stays as a **head+tail** defense-in-depth
clamp for results that never went through the shared reader (MCP tools,
in-process tools) — it no longer fights the tool, because shared-reader results
already fit under `N`.

### D3 — One shared bounded-output reader, two entry points over a shared core

Extract `BoundedDrainAsync` into a reusable type (e.g.
`Netclaw.Actors/Tools/BoundedOutputReader.cs`) exposing:

- `DrainToWindowAsync(TextReader source, int budget, CancellationToken) →
  (string Text, bool Truncated)` — head+tail window, discard middle, bounded
  memory. Used by `file_read` (and any in-memory-only bound).
- `DrainCaptureAsync(TextReader source, int captureMax, int inlineBudget,
  CancellationToken) → (string Captured, string Inline, bool Truncated)` —
  retains up to `captureMax` (the spill body) and an `inlineBudget` head+tail
  window, both in bounded memory; the pipe drains past `captureMax` so a live
  child never deadlocks. The **caller** redacts `Captured` with
  `SecretOutputRedactor`, writes it to the spill path, and surfaces `Inline` +
  the path. For `shell_execute` and `background_job`.

Both share the existing chunked-read + lazy-tail-ring core (pooled buffer,
`ValueTask` reads, block-copy ring) proven out in #1293. The window logic is
written once. The reader stays a pure leaf — no redaction, no file IO — so it
keeps the low-coupling property the #1293 review asked for.

- *Alternative — `Queue<char>` / `System.IO.Pipelines`:* the #1293 review weighed
  this; the existing ring is already correct, tested, and allocation-flat, so
  reuse it rather than re-platform.

### D4 — Spill file: `sessionDir/tool-calls/{toolCallId}.log`; plumb `ToolCallId`

Spill files live under the existing session directory, namespaced per call.
`ToolExecutionContext` gains `ToolCallId` (set by the pipeline at context-build
time, where the call ID is already known). The session directory's access scope
governs the spill file; lifecycle/cleanup is deferred (Non-Goal).

### D5 — Two-mode redaction, reusing the existing `SecretOutputRedactor`

Both modes use the existing `Netclaw.Security.SecretOutputRedactor.Redact` — **no
new redaction abstraction**.

- *Redact-on-read:* `file_read` runs `SecretOutputRedactor.Redact` over its
  bounded return value (cheap; content is already `≤ N`). Closes the cleartext
  gap and covers foreign files uniformly.
- *Redact-on-write:* the captured spill body is bounded to `captureMax`
  (`MaxOutputChars`, D8) and held in a bounded buffer, then redacted in a single
  `SecretOutputRedactor.Redact` pass before the file is written. Because the
  buffer is bounded, this stays within budget **and** preserves the redactor's
  multi-line patterns (the `PRIVATE KEY` block) that a per-chunk or per-line pass
  would split. The inline `N` window is a head+tail view of the already-redacted
  buffer.

- *Alternative — a new streaming/line redactor (`IStreamingRedactor`):* rejected.
  Reusing `SecretOutputRedactor` on a bounded buffer avoids a parallel redaction
  path that would drift from the canonical pattern set, at the cost of capping
  spill capture at `MaxOutputChars` rather than persisting a truly unbounded log.
  The only consumer that may want an unbounded complete log is `background_job` —
  see Open Questions; that is the sole case where a streaming redactor could
  later earn its keep.

### D6 — `file_read` bounds in place; no spill (the file *is* the backing)

For an over-budget file, `file_read` returns an `N` head+tail sample plus a steer
to `offset`/`limit` or `grep` — it does **not** copy the file to a spill path
(the file already exists; pointing at itself is the affordance). Because file size
is known up front, `file_read` reads only a bounded prefix/suffix rather than
materializing the whole file. The offset/limit path (`ReadLinesAsync`) is already
bounded and is unchanged.

### D7 — Combined (not per-stream) budget for `shell_execute`

`shell_execute` bounds the combined stdout+stderr inline result to a single `N`
(resolving the per-stream doubling from the #1293 review). Implementation: both
pipes are teed to the spill concurrently (full, labeled), while the inline window
is built from the assembled stdout-then-stderr under one shared `N` budget. The
loss of fine-grained stdout/stderr interleaving in the inline window is accepted;
the full interleaved-by-stream output is available in the spill file.

### D8 — `MaxOutputChars` is the capture ceiling **and** the in-memory redaction bound

With `N` (`MaxInlineToolResultChars`) owning the inline budget,
`ToolConfig.MaxOutputChars` is repurposed as the **maximum total output captured**
— the size of the bounded buffer that becomes the spill body. One knob, two roles
that fall out together: it caps disk/memory against a flood, and it bounds the
buffer that D5 redacts in a single `SecretOutputRedactor` pass (which is what lets
us reuse the existing redactor instead of inventing a streaming one). The pipe
still drains past it to avoid child deadlock, discarding the excess with a
"capture ceiling exceeded" marker. `MaxOutputChars` should be raised from its
current 32000 to a comfortable spill size (e.g. a few hundred KB) so the spill is
useful while staying redactable in memory.

## Risks / Trade-offs

- **Capture ceiling (`MaxOutputChars`) clips the spill for a true flood** → the
  spill holds a bounded head+tail capture, not every byte of a multi-hundred-MB
  log; acceptable for inspection (the model reads ranges / greps), and redaction
  stays correct and complete because it runs once on the bounded buffer with the
  existing redactor. `background_job` is the one consumer that might want a
  byte-complete log — see Open Questions.
- **`N` = 2000 is small; agents may read-range more often** → that is the
  intended behavior (steer to `grep`/ranged reads); the full output is one
  `file_read` away. Mitigate with a clear steering message.
- **Combined budget loses inline stdout/stderr interleaving** (D7) → full
  per-stream output preserved in the spill file.
- **Lowering `MaxInlineToolResultChars` default is globally breaking** → it is a
  configurable knob; document the change; existing configs that set it
  explicitly are unaffected.
- **`ToolCallId` plumbing touches the context build path** → additive field;
  tools that ignore it are unaffected.

## Migration Plan

1. Land the shared reader (no behavior change yet); redaction reuses the existing
   `SecretOutputRedactor`.
2. Switch `shell_execute` to the shared reader with spill (behavior change behind
   the new default).
3. Switch `background_job` and `file_read`; add `file_read` redact-on-read.
4. Flip `MaxInlineToolResultChars` default to 2000 and make `ClampToolResult`
   head+tail.
5. Update `netclaw-operations` skill + eval cases for the new tool-output shape.

Rollback: revert the default to 12000 and the shared-reader wiring; the #1293
in-tool bound remains, so the OOM fix is not lost.

## Open Questions

- **`background_job` byte-complete log:** capping the job log at `MaxOutputChars`
  (D8) means a long job's log is no longer byte-complete. If completeness matters
  for that path, it needs either a larger ceiling or genuine streaming redaction
  — the one place a streaming redactor would earn its keep. Leaning: cap it like
  the others; revisit if a user needs full job logs.
- Should the steering message vary when a Task/explore sub-agent is available
  (opencode delegates "do not read the full file yourself")? Out of scope unless
  cheap.
