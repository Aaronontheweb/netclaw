## Why

The Netclaw `0.27.0-beta.4` session shows repeat approval prompts for complete shell commands whose verbs already have grants.
The current policy loses reusable candidates when a directory change precedes a pipeline and later statements.

## What Changes

- Netclaw will give a typed directory correction for an eligible complete shell call before a prompt.
- Netclaw will derive reusable candidates only when it can prove every possible command directory and path scope.
- ShellSyntaxTree will publish general syntax facts for a bounded set of currently unsupported Bash forms.
- Netclaw will consume a released ShellSyntaxTree beta and keep unknown forms under exact approval.
- The work will add sanitized cases from the observed session to the approval corpus.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: Define the directory correction and complete compound-command grant boundary.

## Impact

The change affects `ShellPolicyCoordinator`, `BashCausalApprovalIntent`, the shell matcher, approval evidence, and the approval runbook.
It affects the ShellSyntaxTree Bash parser and the Netclaw package version.
The change supports PRD-002 and PRD-006. It adds no new shell authority or grant type.

## Security and operational impact

Every possible verb and path scope must have a grant or a reviewed-safe rule before execution.
Unknown syntax, unknown paths, redirects, protected paths, hard denials, and external symlinks keep their current gates.
The correction changes no command and executes no process. A replacement call passes normal policy.

## Scope

This MVP change covers bounded Bash facts and local shell approval decisions.
It does not infer runtime command output, enumerate globs, or approve an ungranted executable.
