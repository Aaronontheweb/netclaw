## 1. Spec Delta

- [x] 1.1 Add the `Symlink under approved root cannot reach a protected path` scenario under `Requirement: Directory-root approvals for shell_execute`
- [x] 1.2 Update the requirement description to explicitly call out symlink resolution along every path component as the safety-backstop contract

## 2. Verify Existing Implementation Backs the Scenario

- [x] 2.1 Confirm `ToolPathPolicy.TryResolveSymlinksInPath` exists and is wired into `CommandReferencesDeniedPath` (already shipped in commit `62d711d9`)
- [x] 2.2 Confirm `CommandReferencesDeniedPath_blocks_symlink_escalation_into_protected_path` regression test exists and passes (already shipped in commit `62d711d9`)

## 3. Sync and Archive

- [x] 3.1 Run `/opsx-sync` to apply the delta to `openspec/specs/tool-approval-gates/spec.md`
- [x] 3.2 Run `/opsx-archive` to move this change into `openspec/changes/archive/`
