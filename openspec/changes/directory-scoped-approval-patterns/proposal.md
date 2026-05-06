## Why

Path-aware shell commands can produce per-file exact approval patterns (e.g.,
`cat /home/.netclaw/logs/crash-foo.log`). In a single
diagnostic session (D0AC6CKBK5K/1778085593.830269), the user was prompted **11
separate times** for commands targeting different files in the same directory.
The single-token exact-match restriction prevents bare `cat` from silently
approving `cat /etc/shadow`, but the per-file granularity is too annoying for
legitimate diagnostic work.

## What Changes

- "Approve for this chat" (B) and "Approve always" (C) store **directory-scoped
  patterns** (e.g., `grep /home/.netclaw/logs/`) instead of per-file patterns
  only for a narrow direct read/list allowlist: `cat`, `less`, `more`, `head`,
  `tail`, `grep`, and `ls`.
- Relative path operands are resolved against the shell tool `WorkingDirectory`
  before both exact-pattern extraction and directory-scope extraction/matching.
- Path-aware exact approval patterns use the actual normalized path operand when
  one exists, including commands like `grep -l "timeout" logs/app.log` where the
  search term is not the path operand and commands like `find`, `bash`, or
  `python3` when a direct path operand exists.
- Existing directory operands keep their directory scope for allowlisted
  directory-scoped verbs instead of widening to the parent directory.
- A trailing `/` on a stored approval pattern signals directory scope. Matching
  uses `PathUtility.IsWithinRoot()` for boundary-safe containment instead of
  naive `StartsWith`.
- Minimum depth enforcement (2 path segments below root) prevents overly broad
  scopes like `/` or `/etc/`.
- `IToolApprovalMatcher` gains `ExtractDirectoryPatterns()` for tool-specific
  directory pattern extraction.
- Commands outside the directory-scope allowlist, including `find`, fall back to
  exact approval patterns and generic B/C labels rather than directory scope.
- Shell redirection operators disable directory-scoped extraction even for
  allowlisted verbs, causing fallback to exact approval patterns and generic
  labels.
- Approval option labels only show a directory-specific scope when the full
  approval set for the request maps cleanly to a single directory; otherwise the
  labels stay generic.
- Pipe segments can get directory scope when the segment has a direct path
  operand, but MVP extraction does not infer indirect path flow through `xargs`,
  `eval`, loop variables, or similar shell constructs.
- "Approve once" (A) continues to use exact patterns — directory scope only
  applies to broader grants.
- Windows-native shell path semantics are out of scope for this change and are
  tracked separately in issue #899.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `tool-approval-gates`: Adds directory-scoped pattern extraction, storage,
  matching, and display for shell command approvals. Extends the existing
  pattern matching, `IToolApprovalMatcher` interface, persistent approval
  storage, and `ToolInteractionRequest` protocol requirements.

## Impact

- **Security**: Only relaxes the interactive approval gate. Hard deny list,
  `ToolPathPolicy` (protected paths at execution time), symlink resolution, and
  path traversal prevention layers are unaffected. Within an approved directory,
  `ToolPathPolicy.CommandReferencesDeniedPath()` still independently blocks
  access to protected files like `config/netclaw.json`.
- **Code**: `ShellTokenizer`, `ApprovalPatternMatching`, `IToolApprovalMatcher`
  (+ all implementations), `ToolAccessPolicy`, `ToolApprovalContext`,
  `ToolInteractionRequest`, `PendingToolInteraction`, `LlmSessionActor`,
  `SessionToolExecutionPipeline`.
- **Backward compatibility**: Existing non-slash patterns continue to work
  unchanged. `DirectoryPatterns` defaults to empty list on protocol types.
