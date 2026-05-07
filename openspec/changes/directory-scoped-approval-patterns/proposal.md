## Why

Path-aware shell verbs (`ls`, `cat`, `grep`, `find`, etc.) produce per-file
approval patterns (e.g., `cat /home/.netclaw/logs/crash-foo.log`). In a single
diagnostic session (D0AC6CKBK5K/1778085593.830269), the user was prompted **11
separate times** for commands targeting different files in the same directory.
The single-token exact-match restriction prevents bare `cat` from silently
approving `cat /etc/shadow`, but the per-file granularity is too annoying for
legitimate diagnostic work.

## What Changes

- "Approve for this chat" (B) and "Approve always" (C) store **directory-scoped
  patterns** (e.g., `grep /home/.netclaw/logs/`) instead of per-file patterns
  when the command targets a recognizable file path.
- A trailing `/` on a stored approval pattern signals directory scope. Matching
  uses `PathUtility.IsWithinRoot()` for boundary-safe containment instead of
  naive `StartsWith`.
- Minimum depth enforcement (2 path segments below root) prevents overly broad
  scopes like `/` or `/etc/`.
- `IToolApprovalMatcher` gains `ExtractDirectoryPatterns()` for tool-specific
  directory pattern extraction.
- Approval option labels dynamically show the directory scope (e.g., "Approve
  `grep` in /home/.netclaw/logs/ for this chat").
- "Approve once" (A) continues to use exact patterns — directory scope only
  applies to broader grants.

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
