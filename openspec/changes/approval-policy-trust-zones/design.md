## Context

The current v2 approval architecture (this codebase, never shipped) stores
`(verb, directory)` cross-product entries in a single per-audience store. The
matcher anchors decisions on the spawn cwd, which collides with the user's
mental model whenever a compound command like `cd /target && cmd` is involved
— the user thinks "approve in `/target`," the matcher records "approve in
`session_dir`."

The session-cwd capability today defines `WorkingContext.ProjectDirectory`
(set via the `set_working_directory` tool) as the load-bearing input to the
approval gate's safe-space root set. Live evidence shows the agent rarely
calls this tool and even when it succeeds, the agent defensively prepends
`cd <abs-path> && ...` to every shell call anyway — meaning the
`ProjectDirectory` mechanism is paying a complexity cost (auto-injection
of project-context files, cwd reasoning in the matcher, persistence in
`SessionSnapshot`) for behavior the agent doesn't actually rely on.

The shell parser (`Netclaw.Security.ShellTokenizer`) is regex-based and
produces flat token lists. Adding the per-verb path-extraction rules,
cd-in-compound propagation, and redirect-target detection that the new gate
model needs would essentially require building a small AST inside Netclaw.
That work is parser-shaped, not policy-shaped, and belongs in a focused
library rather than embedded in the security namespace.

Stakeholders:
- Daemon operators configuring trust profiles per audience.
- End users (Slack/Discord) responding to approval prompts.
- Agent runtime (LLM sessions) observing project-context lookup discipline.
- Future Netclaw consumers reusing ShellSyntaxTree.

## Goals / Non-Goals

**Goals:**

- Two independent decision axes (geography, action) with two independent
  persistence stores. Either axis can be granted without entangling the other.
- Trust as configuration, not state. Operators declare baseline; users extend
  via prompts; agents never widen trust by issuing commands.
- Read-only verbs auto-pass *only inside trusted zones*. Outside-zone access
  always prompts regardless of verb safety.
- Sequential 4-button prompts on both gates. Identical button shape.
- Externalized shell parser (`ShellSyntaxTree`) consumed via `IShellParser`.
  Bash-first, multi-shell-ready.
- Project context discovered on demand by the agent via explicit lookup
  discipline, not auto-injected by the daemon.

**Non-Goals:**

- PowerShell parsing in v0.1 of `ShellSyntaxTree` (interface seam present;
  concrete impl deferred).
- Migration tooling for existing `tool-approvals.json` shapes (v2 unshipped).
- Per-zone audit log with retention controls (current TUI list is
  sufficient for MVP).
- Cross-audience zone inheritance (each audience configures its own zones).
- Auto-promoting agent-issued `cd` to a trusted zone (explicitly rejected
  on security grounds).

## Decisions

### Decision 1: Two-gate composition over `(verb, directory)` cross-product

**Choice:** Maintain two independent per-audience stores
(`trustedZones`, `verbPatterns`); evaluate each gate independently; both must
pass for silent execution.

**Rationale:** The cross-product encodes a coupled decision the user
doesn't actually make. When a user clicks "Always for `git push` here," they
mean *either* "I trust this directory" *or* "I trust the `git push` shape
generally" — the v2 store conflates them. Splitting allows precise grants:
trust `/etc/nginx` without granting any verb; trust `git push *` without
naming a directory.

**Alternatives considered:**
- *Keep `(verb, directory)` shape, fix matcher header semantics only.* Doesn't
  solve dead-on-arrival session-dir entries or the cross-product confusion.
- *Single combined store with optional fields.* Simpler schema, but the
  evaluation logic still has to branch on what's present, which is the same
  cost as two stores with cleaner semantics.

### Decision 2: Colocate stores in a single `tool-approvals.json` per-audience

**Choice:** One file, per-audience top-level keys, each containing
`verbPatterns` and `trustedZones` arrays:

```json
{
  "personal": {
    "verbPatterns": ["git push *"],
    "trustedZones": ["/etc/nginx"]
  },
  "team": {
    "verbPatterns": [],
    "trustedZones": ["/opt/shared"]
  }
}
```

**Rationale:** Both stores have the same lifecycle (mutated by prompt clicks,
read by gate evaluator). Two files would split a single conceptual
"runtime approval state" into two atomic-write units. Per-audience grouping
keeps the operator's mental model intact (today's audiences in `netclaw.json`
already group their config).

**Alternatives considered:**
- *Two separate files (`tool-approvals.json` + `trusted-zones.json`).*
  Sharper separation of concerns; doubles atomic-write coordination on
  prompt response.
- *Mutate `netclaw.json` trust profiles directly.* Mixes operator-declared
  static config with prompt-extended runtime state in one file. Loses the
  static/state distinction.
- *Inside `tool-approvals.json` with sectioned schema.* Chosen.

### Decision 3: Glob verb pattern format

**Choice:** Verb patterns stored as glob strings (`git push *`,
`rm /tmp/*`, `dotnet test *`).

**Rationale:** Globs allow geography-conditional grants on the verb pattern
itself (`rm /tmp/*` allowed; `rm /home/*` denied). Matches OpenCode's
validated approach. Future-proof for more expressive patterns
(e.g., `git push origin *`).

**Alternatives considered:**
- *Verb-chain only (`git push`).* Simpler matcher; geography conditioning
  lives entirely in the zone gate. Loses expressiveness for cases like
  "always allow `rm` under `/tmp`."
- *Hybrid (verb-chain stored, glob optional).* Forks the matcher code path
  for marginal benefit; choose one and commit.

### Decision 4: Sequential 4-button prompts, both gates identical shape

**Choice:** When a single shell call hits both gates, fire two prompts
back-to-back. Each uses `[Once / Session / Always / Deny]`. Same shape on
both gates; only the question text differs.

**Rationale:** Batched prompts for two independent persistence axes require
a 2D button matrix (zone-scope × verb-scope) that exceeds Slack's block
budget and overwhelms the user's working memory. Sequential keeps each
decision unambiguous. The 4-button row has been UX-validated at v2 already;
the 5-button v2 was driven by the (here / anywhere) cross-product which we
explicitly killed.

Worst-case prompt count is 2; common case (one or both gates pre-trusted)
is 0–1.

**Alternatives considered:**
- *Batched single prompt with combined buttons.* Densest UI,
  conceptually muddy ("this button extends both stores from one click").
- *Adaptive per channel.* Doubles render code paths and diverges the user's
  mental model across channels.
- *Drop the Session scope.* Loses the "just for this task" middle ground that
  users want when granting experimentally.

### Decision 5: Session-scoped grants in-memory on `LlmSessionActor`

**Choice:** Session-scope grants live in two segments
(`SessionTrustedZones`, `SessionVerbPatterns`) on `LlmSessionActor` instance
state. Not persisted to disk. Garbage-collected when the actor terminates.

**Rationale:** Session-scope means "for this conversation only." Persisting
to disk would invite stale grants surviving daemon restarts where the user's
intent was clearly bounded. Akka actor lifecycle is the natural lifetime
boundary.

**Failure modes:**
- *Daemon restart mid-session.* Session-scope grants lost; user re-prompted
  if they re-issue the same command. Acceptable — daemon restart is a
  significant event and re-prompt is cheap.
- *Actor recovery from snapshot.* Snapshots don't include session-scope
  grants by design (they're explicitly in-memory-only). Recovery starts
  fresh; same re-prompt behavior as restart.

### Decision 6: Per-call approval workflow as actor-internal state machine

**Choice:** Add a `ToolApprovalWorkflow` value type to `LlmSessionActor` per
in-flight approval. State transitions: `Start → ZoneGate → VerbGate → Complete`.
Workflow is purely local to the call; no cross-call coordination.

```
ToolApprovalWorkflow:
  Call:        ToolCall
  Paths:       List<string>            // extracted from AST
  ZoneState:   ApprovalScope?          // null until prompt 1 resolves
  VerbState:   ApprovalScope?          // null until prompt 2 resolves
  Stage:       Start | ZoneGate | VerbGate | Complete
```

**Rationale:** Encapsulates the state cleanly. No new actors needed —
existing `ToolApprovalRequest` / `ToolApprovalResponse` messages serialize
twice in worst case. The workflow record is small and lives alongside the
existing per-call pending-state.

**Failure modes:**
- *User takes >watchdog timeout on either prompt.* Watchdog is paused for
  the entire approval flow (across both prompts), same pattern as v2.
- *Approval response received for stale prompt (e.g., user clicks Slack
  button after daemon restart).* Workflow state is lost on restart;
  late-arriving responses are ignored with a log entry. Same as v2.
- *Concurrent tool calls each in their own workflow.* Each LlmSessionActor
  call has its own workflow instance; no shared mutable state between
  workflows on the same actor.

### Decision 7: Externalize shell parser to `ShellSyntaxTree` repo

**Choice:** Spin out a new repo at `github.com/Aaronontheweb/ShellSyntaxTree`
publishing a NuGet package of the same name. Bash-first, multi-shell-ready
via `IShellParser` interface. Develop in parallel with sibling
`<ProjectReference>` during this change; swap to `<PackageReference>` once
v0.1 publishes.

**Rationale:** Bash parsing is a generic capability with broader applicability
than Netclaw's security gates. Separating it improves test focus (parser
tests live with the parser, not interleaved with policy tests), allows
independent versioning, and avoids growing Netclaw's attack surface with
parsing logic that has nothing to do with Akka actors or approval policy.

**Alternatives considered:**
- *tree-sitter-bash via P/Invoke.* Highest correctness ceiling but the .NET
  binding ecosystem is thin; native libs would need shipping per platform
  (Linux x64/arm64, macOS x64/arm64, Windows x64), and PowerShell would
  need a second native library. Rejected: packaging cost outweighs ceiling
  gain for our use case.
- *Hand-roll an AST inside `Netclaw.Security`.* Bloats the security namespace
  with parser code that other consumers might want; couples Netclaw releases
  to parser bug fixes.
- *Iterate on existing regex `ShellTokenizer`.* Lowest ceiling; doesn't
  resolve the structural-vs-flat-tokens limitation that the new gate model
  requires.

**Failure modes:**
- *ShellSyntaxTree v0.1 not yet published when Netclaw needs it.* Sibling
  `<ProjectReference>` during dev unblocks; CI gating on package publish
  comes after v0.1 is up.
- *Parser misextracts paths from a novel shell idiom.* Gate evaluator
  defaults to the safe behavior (treat as untrusted, prompt). Failure
  mode is "user sees an unnecessary prompt," not "agent silently
  bypasses approval."
- *Dynamic-content paths (`$VAR`, unresolved expansion).* Parser flags as
  dynamic; gate evaluator treats as path-arg-less. Better to under-extract
  (and let the verb gate handle it) than to misextract a literal `$VAR/foo`
  as a path.

### Decision 8: Project context via on-demand `file_read`, not auto-injection

**Choice:** Delete the `project-instructions` capability's auto-injection
machinery. Add explicit lookup discipline to `Resources/AGENTS.md` instructing
the agent to read `.netclaw/AGENTS.md` → `AGENTS.md` → `CLAUDE.md` →
`CONTEXT.md` once per project per session via `file_read`.

**Rationale:** Auto-injection costs ~6k tokens per session (observed in
dogfood) and depends on `WorkingContext.ProjectDirectory`, which the agent
rarely sets. On-demand reading is cheaper, follows the same lookup order,
and survives the deletion of `ProjectDirectory` cleanly.

**Failure modes:**
- *Agent forgets to read project context.* Same effective behavior as a
  session today where `ProjectDirectory` is unset (no project content). The
  AGENTS.md guidance frames this as a discipline rather than a guarantee.
- *Project file changes mid-session.* On-demand read happens once at
  project entry; subsequent changes aren't seen until the agent re-reads.
  Same as v2 (which only re-injected on `set_working_directory` calls).

## Risks / Trade-offs

- **[Risk]** Two-prompt sequential UX feels chatty for users who haven't
  pre-trusted anything. → **Mitigation:** Common case (read-only verb in
  trusted zone) hits zero prompts. Worst case (mutating verb in untrusted
  dir) hits two prompts but each has a 4-button row that includes "Always"
  to amortize. Track prompt-count metrics post-launch.

- **[Risk]** ShellSyntaxTree v0.1 ships with parser bugs that affect
  approval correctness. → **Mitigation:** Test corpus seeded from sanitized
  real-agent emissions provides regression coverage. Gate evaluator's
  default-to-prompt fallback turns parser misses into "extra prompt," not
  "silent bypass."

- **[Risk]** Operators have v2-shape `tool-approvals.json` from running
  pre-release Netclaw. → **Mitigation:** Wipe-and-regenerate on first start
  with the new schema; communicate in release notes. v2 was never shipped
  beyond development environments.

- **[Risk]** Removing `set_working_directory` and `WorkingContext.ProjectDirectory`
  may surface dependencies we haven't enumerated (e.g., a CLI command, a
  context layer, a snapshot field). → **Mitigation:** Implementation tasks
  include a "find all references" sweep; eval suite covers the agent
  behaviors that previously depended on these.

- **[Trade-off]** Session-scope grants don't survive daemon restart. Users
  who granted a Session-scope mid-task and restart the daemon will be
  re-prompted. Acceptable — daemon restart is uncommon and re-prompt
  preserves the user's intent boundary ("for this conversation").

- **[Trade-off]** External dependency on `ShellSyntaxTree` introduces a
  release coordination burden (Netclaw release blocked on parser package
  availability). Sibling `<ProjectReference>` during dev mitigates this
  for the rewrite itself; future Netclaw releases consuming new parser
  features will need coordinated publishes.

- **[Trade-off]** Single per-audience file (`tool-approvals.json`) means
  operators wanting to back up just zones (or just verbs) must edit JSON
  by hand. Acceptable — TUI provides per-axis visibility and revoke; raw
  file is for advanced operators only.

## Migration Plan

**Pre-deployment (within this change):**

1. Stand up `github.com/Aaronontheweb/ShellSyntaxTree` repo with v0.1
   skeleton (lexer + parser + AST + corpus). Tag v0.1.0-alpha for
   PackageReference once stable.
2. Develop Netclaw rewrite in parallel against sibling `<ProjectReference>`.
3. Once both stable, switch Netclaw's `.csproj` to `<PackageReference>` for
   `ShellSyntaxTree`.
4. Run eval suite end-to-end before merge.

**Deploy:**

- Single daemon restart with new binary.
- On first start, `tool-approvals.json` is read; if v2-shape detected,
  archive to `.v2-discarded.bak` and start with empty new-shape file.
- No data migration; users re-approve as they hit prompts.

**Rollback:**

- Revert binary; restore `.v2-discarded.bak` to `tool-approvals.json`.
- v2 schema and code paths are removed in this change, so rollback is a
  full revert (no partial fallback).

## Open Questions

1. **Eval suite coverage for two-gate transitions.** Current evals cover
   single-prompt v1 behavior. Need new cases for: zone-then-verb sequencing,
   read-only-in-trusted-zone silent path, multi-path zone batching, session
   vs always scope persistence. Captured in `tasks.md`.

2. **Multi-audience handling at evaluation time.** A session is bound to one
   audience (per identity); the matcher reads only that audience's stores.
   Confirm this matches the existing `IToolApprovalMatcher` contract — likely
   it does, but explicit verification during implementation.

3. **Zone glob matcher precedence.** When `trustedZones` contains both
   `/home/user/repos/*` and a more-specific `/home/user/repos/secret`,
   precedence rules need definition (probably "any match passes" given
   trust is additive, but worth a scenario in the spec).

4. **TUI revoke semantics for partial state.** If a user revokes
   `/etc/nginx` from `trustedZones`, does it also affect any in-memory
   session-scope grants in active sessions? Probably no (session-scope is
   a separate axis), but spec should be explicit.
