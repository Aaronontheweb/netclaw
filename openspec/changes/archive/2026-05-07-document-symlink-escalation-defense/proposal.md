## Why

PR #896 ships directory-scoped shell approvals that let a user grant
`shell_execute` access to a directory root (e.g., `/home/user/safe/`)
once and reuse it for later commands under that root. The change's
own `design.md` lists `ToolPathPolicy` as the third security layer that
"remains a backstop" against protected-path access even after a root
grant. PR review found that backstop was paper-only against
symlink-in-middle escalation: `Path.GetFullPath` collapses `.`/`..` but
does not follow symlinks in any path component, so a planted
`/home/user/safe/leak -> /etc` would let `cat /home/user/safe/leak/passwd`
read `/etc/passwd` without the static check ever seeing `/etc/`.

Commit `62d711d9` on PR #896 added `ToolPathPolicy.TryResolveSymlinksInPath`,
which walks every component of a candidate path and resolves symlinks
along the way. The fix is in production. The OpenSpec capability
`tool-approval-gates` does not yet describe that contract — there is no
scenario pinning the symlink-escalation block to the spec, only a
regression test in `ToolPathPolicyTests`. This change closes that gap so
the next refactor cannot silently regress the defense.

## What Changes

- Add a scenario under the existing `Requirement: Directory-root approvals
  for shell_execute` describing that a symlink under an approved root
  cannot reach a protected path: the approval gate auto-approves the
  command (path is within the approved root) but `ToolPathPolicy`
  blocks execution because the canonical path resolves to a denied root.
- No code changes. The implementation already exists.
- No new requirements. This is a scenario-only addition that documents a
  contract the existing requirement already alludes to ("the safety
  backstop").

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `tool-approval-gates`: Add a scenario under
  `Requirement: Directory-root approvals for shell_execute` that
  describes the symlink-escalation defense.

## Impact

- **Security**: Documents an existing defense; no behavioral change.
- **Code**: None. Implementation already merged in commit `62d711d9`.
- **Tests**: Existing `CommandReferencesDeniedPath_blocks_symlink_escalation_into_protected_path`
  in `ToolPathPolicyTests` covers the scenario.
- **Backward compatibility**: N/A. Documentation-only.
