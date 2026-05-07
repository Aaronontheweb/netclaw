## Context

The directory-scoped approval feature relies on a layered security model:
1. Hard deny list (before approval gate)
2. Interactive approval gate (`ToolAccessPolicy` + `IToolApprovalService`)
3. `ToolPathPolicy` protected-path enforcement (at execution time, after approval)

Layer 2 was relaxed by `directory-scoped-approval-patterns`: a single
"Approve always" on `/home/user/safe/` covers every later command whose
recognized local paths fall under that root. The promise that broader
approvals do not bypass protected-path access rests entirely on layer 3.

Before commit `62d711d9` on PR #896, layer 3 was incomplete against
symlink escalation. `ToolPathPolicy.CommandReferencesDeniedPath` ran
`Path.GetFullPath` over each path token, which lexically resolves `.`
and `..` but does not follow symlinks. A planted
`/home/user/safe/leak -> /etc` directory symlink would let
`cat /home/user/safe/leak/passwd` reach `/etc/passwd` without the
static path check ever observing `/etc`. The shell syscall follows
symlinks; the static analysis did not.

Commit `62d711d9` added `TryResolveSymlinksInPath`, which walks each
path component, calls `DirectoryInfo.ResolveLinkTarget(returnFinalTarget:
true)` on every existing ancestor, and rebuilds the canonical path. The
denied-path check now runs against both the lexical normalization and
the symlink-resolved canonical form. Regression coverage exists in
`CommandReferencesDeniedPath_blocks_symlink_escalation_into_protected_path`.

This change is documentation-only. It pins the existing defense to the
`tool-approval-gates` spec so the contract is testable from the spec
down, not just from a single regression test that a future refactor
could remove.

## Goals / Non-Goals

**Goals:**

- Document the symlink-escalation defense as a scenario under the
  existing `Requirement: Directory-root approvals for shell_execute`.
- Make the layered-defense promise (approval gate + `ToolPathPolicy`)
  testable from the spec.
- Establish that `ToolPathPolicy` resolves symlinks along every path
  component, not just at the leaf.

**Non-Goals:**

- Changing implementation. The defense is already in production.
- Adding new requirements. The scenario fits under the existing
  requirement's safety-backstop clause.
- Documenting the broader symlink-handling contract for non-shell
  surfaces (`IsDenied`, `IsReadDenied`). Those have their own existing
  behavior via `TryResolveSymlinkTarget` and are out of scope here.
- Adding Windows-specific scenarios. Symlink creation requires elevation
  on Windows; coverage there is tracked under issue #899.

## Decisions

### Add a scenario, not a new requirement

The existing requirement already states:
> "The system SHALL enforce minimum directory depth, path normalization,
> boundary-safe containment, path traversal checks, and `ToolPathPolicy`
> as the safety backstop for directory-root approvals."

That clause covers the contract. What's missing is a specific, testable
scenario for the symlink case. A scenario sits under the existing
requirement rather than introducing a new one.

### Scenario uses POSIX paths

The regression test that backs this scenario is POSIX-only because
non-elevated symlink creation on Windows requires Developer Mode and
makes test surfaces flaky. The spec scenario uses `/home/.netclaw/...`
to match the existing scenarios in `tool-approval-gates/spec.md`. Windows
coverage is tracked under issue #899.

### No tasks beyond the spec edit

The implementation is already shipped (`62d711d9`) and has regression
coverage. The only task is the spec delta itself, plus a verification
step that the existing test maps to the new scenario.

## Risks / Trade-offs

**[Risk] Documentation-only change can be deprioritized** → Mitigated by
the OpenSpec workflow: the scenario must exist for `opsx-archive` to
consider this contract closed. Skipping the doc means the next refactor
of `ToolPathPolicy` has no spec scenario to point at when reviewing
whether symlink behavior is preserved.

**[Risk] POSIX-only scenario could imply Windows isn't covered** → The
underlying `TryResolveSymlinksInPath` helper is platform-agnostic. The
test gap is about test environment, not implementation. Issue #899
captures Windows coverage as a follow-up.
