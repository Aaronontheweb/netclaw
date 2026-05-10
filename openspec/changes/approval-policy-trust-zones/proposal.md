## Why

The v2 approval model — a single per-audience store of `(verb, directory)`
cross-product entries — fails in practice. Live dogfood evidence shows three
recurring failure modes:

1. **Dead-on-arrival entries.** When a session runs commands from
   `session_dir` cwd (no path arg), "Always here" persists `(verb, session_dir)`
   tuples that can never recur because the session directory is unique per
   session. The store fills with garbage that never matches.
2. **Wrong header semantics.** A compound like `cd /target && cmd1 && cmd2`
   asks the user *"Approve in `<session_dir>`?"* when the user's mental model
   is *"Approve in `/target`."* The matcher anchors on cwd; the user reads on
   the cd target. Mismatch.
3. **Conflated axes.** "Where is it safe to operate?" (geography) and "What
   actions are safe to take?" (verb shape) are independent questions that the
   `(verb, directory)` cross-product collapses. The user can't grant `git push *`
   broadly without also granting it to a specific directory; can't trust a
   directory without naming a specific verb.

The rewrite separates these into two independent gates with two independent
persistence stores, anchors trust on operator-declared configuration rather
than session-mutable state, and externalizes shell command parsing to a
dedicated library to keep Netclaw focused on policy.

PRD reference: PRD-002 (Gateway Security Envelope) §5 *"Keep trust boundaries
simple and inspectable"* and §"Privileged operations must be explicitly
approved through trusted operator workflow."

## What Changes

**Trust zones replace `(verb, directory)` entries.**

- Two independent per-audience stores colocated in `~/.netclaw/config/tool-approvals.json`:
  - `trustedZones`: directory globs declaring where this audience may operate silently.
  - `verbPatterns`: command-shape globs (e.g., `git push *`, `rm /tmp/*`)
    declaring what command shapes auto-pass within trusted zones.
- **BREAKING** persistence schema: existing `(verb, directory)` entries discarded
  on first daemon start. v2 was never shipped beyond development; no migration
  written.

**Three-layer gate replaces verb-pattern-only matching.**

- Layer 1 (hard-deny): unchanged.
- Layer 2 (zone gate): per-path check against `trustedZones` ∪ audience baseline
  ∪ `session_dir`. Outside-zone paths prompt the user.
- Layer 3 (verb-pattern gate): only mutating verbs prompt; read-only verbs
  auto-pass *only* inside trusted zones (tightening — no free pass for
  read-only outside zones).

**Sequential prompt UX with identical 4-button rows.**

- Worst case: 2 prompts per call (zone first, then verb). Common case: 0–1.
- Both gates use `[Once / Session / Always / Deny]`. Identical shape, learnable.
- Multi-path commands batch into one zone prompt listing all untrusted paths.

**Shell parsing externalized to ShellSyntaxTree library.**

- New repo: `github.com/Aaronontheweb/ShellSyntaxTree` (NuGet: `ShellSyntaxTree`).
- v0.1: bash-first, multi-shell-ready via `IShellParser` interface.
- Replaces in-tree `Netclaw.Security.ShellTokenizer`. Netclaw consumes the parsed
  AST; gate evaluator owns no parser logic.
- Develop in parallel with sibling `<ProjectReference>`; swap to
  `<PackageReference>` on v0.1 publish.

**Removals (BREAKING within this codebase, no shipped impact).**

- **BREAKING** `set_working_directory` tool deleted. Agent uses `cd` in compound
  commands or absolute paths.
- **BREAKING** `WorkingContext.ProjectDirectory` deleted. Cwd no longer factors
  into approval matcher logic.
- **BREAKING** Project-context auto-injection deleted. Agent reads
  `.netclaw/AGENTS.md` / `AGENTS.md` / `CLAUDE.md` / `CONTEXT.md` on demand
  via `file_read` per explicit lookup discipline in `Resources/AGENTS.md`.

**TUI extension.**

- `netclaw approvals` gains `[Z]ones` and `[V]erbs` tabs. Single discovery
  surface preserved.

## Capabilities

### New Capabilities

None. Trust zones live as a new section within the existing
`tool-approval-gates` capability rather than a standalone capability — the
gates compose at evaluation time and splitting introduces cross-capability
dependencies in the spec without clarifying anything.

### Modified Capabilities

- `tool-approval-gates`: wholesale rewrite. New three-layer gate, two-store
  persistence, sequential 4-button prompts, ShellSyntaxTree consumption,
  TUI Z/V tabs, glob verb pattern format.
- `session-cwd`: **REMOVED**. `WorkingContext.ProjectDirectory` and the
  `set_working_directory` tool both deleted. Cwd no longer participates in
  approval logic; `psi.WorkingDirectory` for spawned subprocesses falls back
  to `SessionDirectory` only.
- `project-instructions`: **REMOVED**. Daemon-side auto-injection of
  project identity files deleted. Agent guidance in `Resources/AGENTS.md`
  takes over via on-demand `file_read`.

## Impact

**In-scope for MVP (this change):**

- All architecture, persistence, and prompt UX described above.
- ShellSyntaxTree v0.1 (bash) consumed via PackageReference at completion.
- Slack and Discord adapter rendering for the new sequential prompt flow.
- TUI Z/V tabs.
- Resources/AGENTS.md project-context lookup discipline.
- Eval suite updates covering the new gate semantics and project-context flow.

**Out-of-scope (deferred):**

- ShellSyntaxTree PowerShell support (v0.x or later — interface seam present
  from v0.1 but no `PowerShellParser` in this change).
- Migration tooling for existing `tool-approvals.json` (none needed; v2
  unshipped).
- Per-zone access auditing UI (current TUI list is sufficient).

**Affected code:**

- `src/Netclaw.Security/` — `ShellTokenizer`, `ShellCommandPolicy`,
  `ShellApprovalSemantics`, `IToolApprovalMatcher`, `ToolApprovalStore`,
  `ToolAudienceProfileResolver` all rewritten or removed.
- `src/Netclaw.Actors/Tools/` — `ToolAccessPolicy` rewritten to three-layer
  gate; `SetWorkingDirectoryTool.cs` deleted; `ShellTool` cwd resolution
  simplified.
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` —
  `PersistApprovalCandidatesAsync` replaced with two-store persistence;
  in-memory session-scope store added; sequential prompt workflow.
- `src/Netclaw.Actors/Sessions/WorkingContext.cs` — `ProjectDirectory` and
  `ResolveShellCwd` simplified to session-dir fallback only.
- `src/Netclaw.Channels.Slack/SlackApprovalBlockBuilder.cs` and
  `src/Netclaw.Channels.Discord/DiscordApprovalPromptBuilder.cs` — two prompt
  shapes, 4-button rows.
- `src/Netclaw.Cli/` — `netclaw approvals` TUI gains Z/V tabs.
- `src/Netclaw.Configuration/Resources/AGENTS.md` — project-context lookup
  discipline section.
- `feeds/skills/.system/files/netclaw-operations/SKILL.md` — operator
  guidance refresh.

**Affected APIs / config:**

- `ApprovalEntry` record retired; replaced by `TrustedZoneEntry` and
  `VerbPatternEntry` records with per-audience grouping.
- `tool-approvals.json` schema changes shape (per-audience top-level keys
  with `verbPatterns` and `trustedZones` arrays).
- `netclaw.json` audience trust profiles unchanged in shape (baseline zones
  already declared); the new operator-extended zones go into
  `tool-approvals.json`, not `netclaw.json`.
- New NuGet dependency: `ShellSyntaxTree` (Aaronontheweb).

**Security impact:**

Threat model: prompt injection. The agent has tool access (`file_write`,
`file_edit`, `shell_execute`) and follows instructions emitted by content
it reads (web pages, file contents, MCP server output). The defense
gates the *mechanism* an injected payload would use, not the agent's
judgment about whether to follow the payload.

- Tightening: read-only verbs no longer auto-pass outside trusted zones.
  Previous behavior allowed any read-only verb anywhere; new behavior
  requires explicit zone trust first. Reduces blast radius of misconfigured
  audience profiles.
- Tightening: agent cannot extend trust by issuing commands. `cd`-in-compound
  is parsed for path attribution but never mutates persistent or session
  state. Closes a class of "agent issues 9-byte command to escalate trust"
  vectors that the old auto-promote design would have opened.
- Tightening: hard-deny defaults compiled into the daemon binary and
  cannot be removed by editing config files. Operator overrides are
  strictly additive (can add deny rules; cannot weaken shipped defaults).
- Tightening: existing `ToolPathPolicy` extended to cover
  `~/.netclaw/config/`. Agent `file_write`, `file_edit`, and
  `shell_execute` clauses targeting paths under this directory are
  hard-denied — no approval prompt offered. Closes the prompt-injection
  vector where the agent would be instructed to rewrite
  `tool-approvals.json` and grant itself global trust.
- New: session-scoped grants exist in-memory only. Lost on session
  termination — by design, prevents accidentally widening trust through
  long-lived persistence of one-off experimental approvals.

**Operational impact:**

- Operator-visible: `netclaw approvals` UI changes shape (tabs).
  Documentation update in `docs/help/approvals.md` and CLI `--help`.
- Operator-visible: existing `tool-approvals.json` contents discarded on
  upgrade. Operators see "no prior approvals" on first daemon start; users
  re-approve as they hit prompts. Communicate in release notes.
- Operator-invisible: ShellSyntaxTree NuGet added; no operator-facing change.
