## Why

In session `D0AC6CKBK5K/1778163885.517639`, repeated shell investigation under
the same working directories created approval fatigue because each new file path
produced another approval prompt. Issue `#905` helps explain why that session
had unusually high shell call volume, but the scope of this change is narrower:
reduce repeated approvals for later shell commands that stay within already
approved local directory roots.

The exact-match restriction is still necessary when the system cannot identify a
safe reusable local root. What needs to change is the granularity of broader
shell approvals, not the safety backstops.

## What Changes

- `Approve once` remains exact blocked-call retry only.
- For `shell_execute`, `Approve for this chat` (B) and `Approve always` (C)
  store **directory roots** instead of verb-specific or command-pattern-specific
  approvals when the approval unit contains recognizable local filesystem paths.
- Directory approvals are root-based and verb-agnostic: later shell approval
  units are auto-approved when all recognized local filesystem paths in that
  unit resolve under already approved roots.
- Shell approval units split on `&&`, `||`, and `;`, but keep pipelines joined
  so commands like `grep ... | wc -l` are covered by one directory-root
  approval.
- If a shell approval unit yields no local directory roots, broader directory
  approval does not apply and the system falls back to exact approval behavior.
- Minimum directory depth, path normalization, path traversal checks, and
  `ToolPathPolicy` remain the safety backstop.
- `DirectoryPatterns` is renamed to `DirectoryRoots` throughout this change.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `tool-approval-gates`: Adds directory-root extraction, storage, matching, and
  display for shell command approvals. Extends shell approval-unit parsing,
  root matching, `IToolApprovalMatcher`, persistent approval storage, and the
  `ToolInteractionRequest` protocol.

## Impact

- **Security**: Only relaxes the interactive approval gate. Hard deny rules,
  minimum root depth, normalization, traversal checks, and `ToolPathPolicy`
  remain unchanged and continue to block protected targets even after a broader
  root approval.
- **Code**: `ShellTokenizer`, shell approval-unit traversal, root matching,
  `IToolApprovalMatcher` (+ implementations), `ToolAccessPolicy`,
  `ToolApprovalContext`, `ToolInteractionRequest`, `PendingToolInteraction`,
  `LlmSessionActor`, `SessionToolExecutionPipeline`.
- **Backward compatibility**: Existing exact approvals continue to work
  unchanged. `DirectoryRoots` defaults to empty on protocol types when no local
  reusable roots are available.
