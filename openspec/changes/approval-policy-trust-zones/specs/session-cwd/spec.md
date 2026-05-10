## REMOVED Requirements

### Requirement: Session-scoped project directory
**Reason:** `WorkingContext.ProjectDirectory` is removed. Trust geography
is now anchored on operator-declared `trustedZones` (per audience) plus
the immutable `session_dir`, not on session-mutable project state. The
agent cannot extend trust by declaring a project root.
**Migration:** Remove `ProjectDirectory` field from `WorkingContext` and
from `SessionSnapshot` serialization. Sessions recovered from older
snapshots SHALL ignore any persisted `ProjectDirectory` field. No
operator-facing migration; the field's removal is invisible because v2
was never shipped beyond development.

### Requirement: set_working_directory tool
**Reason:** The `set_working_directory` tool is removed. Trust zones are
configuration, not state — the agent does not get to mutate trust at
runtime by declaring a project root. Live evidence shows the agent
defensively prepends `cd <abs-path> && ...` to every shell call anyway,
so the tool's spawn-cwd benefit was unused; its `ProjectDirectory`
side-effect is the part being deleted.
**Migration:** Delete `SetWorkingDirectoryTool.cs` and remove all
references from tool registries, audience profiles, and tool exposure
lists. Update `Resources/AGENTS.md` to instruct the agent to use `cd` in
compound commands or absolute paths. Update agent guidance that
previously referenced `set_working_directory` as the gesture for
declaring project scope.

### Requirement: Shell tool cwd defaults to declared safe spaces
**Reason:** The cwd resolution chain simplifies because
`WorkingContext.ProjectDirectory` no longer exists. `ShellTool` SHALL
fall back from explicit `WorkingDirectory` argument directly to
`SessionDirectory`. The approval policy no longer reads cwd to decide
zone membership — the zone gate evaluates the paths the command
operates on (extracted from the AST), not the spawn cwd.
**Migration:** Update `ShellTool.cs` cwd resolution: `args.WorkingDirectory →
SessionDirectory`. Remove `WorkingContext.ProjectDirectory` from
`ResolveShellCwd`. Remove the comment block that referenced
`WorkingContext.ProjectDirectory` as a fallback layer.

### Requirement: Shell tool failure-path hint for cwd outside safe spaces
**Reason:** The hint suggested `set_working_directory <path>` as the
remediation. With that tool deleted, the hint has no remediation to
point at. Denied calls still return a clear denial message; the agent
self-corrects by either using a path under a trusted zone, asking the
user to extend trust, or accepting the deny.
**Migration:** Remove the hint emission code from `ShellTool.cs` and
related plumbing. No replacement hint is added — the prompt itself
already shows the user the path that triggered the denial; the user can
extend trust via the prompt's `Always` button if desired.

### Requirement: set_working_directory expands the approval safe space
**Reason:** With `set_working_directory` removed, this requirement has
no operative tool. Trust zone expansion is now exclusively a user
decision via the zone gate prompt's `Trust this directory` button (with
`Session` or `Always` scope).
**Migration:** Remove the safe-space expansion logic from
`ToolAudienceProfileResolver` and any call sites that reacted to
`ProjectDirectory` changes. The new zone gate's union (audience baseline
∪ persisted `trustedZones` ∪ in-memory session zones ∪ session_dir) is
the only mechanism for expanding the trust boundary.

### Requirement: Working context block includes project directory
**Reason:** With `WorkingContext.ProjectDirectory` removed, the
`[working-context]` block has nothing to emit for project_dir. The
agent reads project context on demand via `file_read` per the explicit
lookup discipline in `Resources/AGENTS.md`; the working-context block
no longer needs to advertise a project root because there isn't one.
**Migration:** Remove the `project_dir:` line from
`WorkingContext.ToContextBlock()` output. If the block becomes empty for
sessions with no recent files either, suppress the entire block from
the system prompt rather than emitting an empty header.
