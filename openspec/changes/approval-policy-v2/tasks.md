# Approval Policy v2 — Tasks

Phasing intent (from design.md):

- **PR 1** = sections 1–6 (storage, matcher, cwd default, safe-verb policy, CLI, hard-deny audit). No prompt/UI changes; channel adapters keep rendering today's body off the new data.
- **PR 2** = sections 7–10 (prompt redesign, resolution message, agent guidance, schedule-creation flow, evals).

Both PRs sit under this single OpenSpec change.

## 1. Storage schema v2 + quarantine

- [x] 1.1 Add `ApprovalEntry` record (`Verb` required, `Directory` nullable) to `src/Netclaw.Configuration/`.
- [x] 1.2 Update `ToolApprovalData` to `Version` (int, default 2) + `Dictionary<string, Dictionary<string, List<ApprovalEntry>>>` shape.
- [x] 1.3 Update `ToolApprovalStore.Load()` to detect `Version != 2` (or absent), move file to `tool-approvals.json.v1.bak`, and return empty v2 store.
- [x] 1.4 Update `ToolApprovalStore.Save()` to always emit `version: 2`.
- [x] 1.5 Update `AddApproval` / `RemoveApproval` / `RemoveAllForTool` / `Snapshot` to operate on `ApprovalEntry`.
- [x] 1.6 Update `ToolApprovalEntryComparer` to compare `(Verb, Directory)` tuples (Ordinal on POSIX, OrdinalIgnoreCase on Windows; null directory compares equal to null directory).
- [x] 1.7 Unit tests for v1 quarantine on first read; round-trip serialization of folder-scoped and global-wildcard entries; comparer on POSIX vs Windows.

## 2. Matcher operates on ApprovalEntry

- [ ] 2.1 Update `IToolApprovalMatcher` to remove `ExtractDirectoryRoots`; pattern extraction returns verb chains only.
- [ ] 2.2 Update `ApprovalPatternMatching` to evaluate `(verb, directory)` containment: candidate matches when verb equals entry's verb AND (entry directory is null OR candidate cwd is under entry directory) AND no symlink segment along the cwd path.
- [ ] 2.3 Plumb `Cwd` through `ToolExecutionContext` / `ToolInteractionRequest` so the matcher always has a concrete cwd to evaluate against.
- [ ] 2.4 Delete the v1 string-shape inspection logic (trailing-slash heuristic) from `ShellApprovalMatcher` and `ApprovalPatternMatching`.
- [ ] 2.5 Unit tests for the four matcher cases: cwd inside entry directory; cwd outside; entry directory null; symlink segment in cwd.

## 3. ShellTokenizer refuses messy input

- [ ] 3.1 Add control-flow keyword detection (`for`/`while`/`do`/`done`/`then`/`fi`/`case`/`esac`) to `SplitCompoundCommand`.
- [ ] 3.2 Add unbalanced-quote/bracket detection (cheap structural scan; no full bash parser).
- [ ] 3.3 When detected, return empty verb-chain list. Do not attempt partial extraction.
- [ ] 3.4 Plumb a "messy" flag through to `ToolInteractionRequest` so the prompt builder can show the "complex command" hint and omit `This chat`/`Always here`/`Always anywhere` buttons.
- [ ] 3.5 Unit tests for: `for ... do ... done`; `while ... do ... done`; `case ... esac`; unbalanced quote; unbalanced bracket; well-formed commands still extract normally.

## 4. ShellTool cwd default

- [ ] 4.1 In `src/Netclaw.Actors/Tools/ShellTool.cs:81-82`, when `args.WorkingDirectory` is null/whitespace, resolve cwd to `WorkingContext.ProjectDirectory` if set, else `session_dir`.
- [ ] 4.2 Thread `WorkingContext` into `ShellTool` via `ToolExecutionContext` (or constructor; whichever matches existing patterns).
- [ ] 4.3 Unit tests: null arg + project_dir set → uses project_dir; null arg + project_dir null → uses session_dir; explicit arg → uses arg verbatim; assert daemon-process cwd is never the resolved value.

## 5. Safe-verbs ∩ safe-space short-circuit

- [ ] 5.1 Create `safe-verbs.linux.json` and `safe-verbs.windows.json` in the daemon's bundled config (alongside other shipped defaults).
- [ ] 5.2 Add a loader that reads bundled defaults and merges `~/.netclaw/config/safe-verbs.<os>.json` overrides if present.
- [ ] 5.3 Create `src/Netclaw.Actors/Tools/ScopedShellSafeVerbPolicy.cs` mirroring `ScopedFileAccessPolicy`. Inputs: candidate verb chain + cwd + `ToolExecutionContext`. Output: short-circuit decision (allow / fall-through).
- [ ] 5.4 Reuse `ToolAudienceProfileResolver` for safe-space root resolution. Personal/Team get `session_dir + project_dir`; Public gets `session_dir` only.
- [ ] 5.5 Reuse `ContainsSymlinkSegment` (or extract to a shared utility) for symlink-segment guard along the cwd path.
- [ ] 5.6 Wire the policy into `ToolAccessPolicy.CheckApprovalGate` so the safe-verb short-circuit runs before the existing approval gate. Hard-deny list (layer 1) still runs first.
- [ ] 5.7 Unit tests covering all four scenarios in the spec: safe verb + project_dir → allow; safe verb + session_dir → allow; safe verb + outside → prompt; mutating verb + safe space → prompt; Public + project_dir → prompt; symlink in cwd → prompt; user override extends defaults.

## 6. CLI updates (list/revoke/trust-verb)

- [ ] 6.1 Update `ApprovalsListView` JSON shape to reflect `ApprovalEntry`.
- [ ] 6.2 Update `ApprovalsCommand list` to render entries with scope labels (`<verb> in <dir>` / `<verb> anywhere`).
- [ ] 6.3 Update `ApprovalsCommand revoke` to accept the user-visible forms above as the pattern argument; route to `RemoveApproval` with parsed `ApprovalEntry`.
- [ ] 6.4 Add `ApprovalsCommand trust-verb <verb> [--audience] [--tool]` subcommand. Idempotent: existing `(verb, null)` entry → exit zero with "no changes".
- [ ] 6.5 Update `ApprovalsManagerPage` (TUI) to show verb + directory columns; revocation + trust-verb both reachable from the TUI.
- [ ] 6.6 Update CLI quarantine-detection note to point at `.v1.bak` (was `.invalid` for v1's malformed-file path; now also fires when v1 is detected during upgrade).
- [ ] 6.7 Tests: `list` stable ordering; `list --json` shape; `revoke` of folder-scoped and global forms; `revoke` no-match exit 1; `trust-verb` adds and is idempotent; `trust-verb` honors audience/tool flags.

## 7. Prompt redesign (Slack)

- [ ] 7.1 Add `ApprovalOptionKeys.ApproveEverywhere` constant ("Always anywhere").
- [ ] 7.2 Update `SlackApprovalBlockBuilder` to render the 5-button row with `Once` / `This chat` / `Always here` / `Always anywhere` / `Deny` and apply `style: "danger"` on `Always anywhere` and `Deny`.
- [ ] 7.3 Update prompt body: header `Approve in <cwd> ?` (or `Approve <verb> in <cwd> ?` for single-verb), bulleted verbs, no `Patterns` / `Directory Roots` sections.
- [ ] 7.4 When the cwd is too shallow (fails minimum-depth check) or the command is "messy" (per task 3.4), omit `This chat`/`Always here`/`Always anywhere` and emit the "complex command" hint.
- [ ] 7.5 Update `SlackApprovalHandler` to map button clicks to the right persistence path: Once → no-op; This chat → session-scoped store; Always here → `(verb, cwd)` per extracted verb; Always anywhere → `(verb, null)` per extracted verb; Deny → refuse this call.
- [ ] 7.6 Update resolution message to the single-line format from the spec.
- [ ] 7.7 Snapshot tests for prompt body (single-verb + compound + messy) and resolution message (Once / This chat / Always here / Always anywhere / Deny).

## 8. Prompt redesign (Discord)

- [ ] 8.1 Update `DiscordApprovalPromptBuilder` to mirror Slack's 5-button row using `ButtonStyle.Danger` on `Always anywhere` and `Deny`.
- [ ] 8.2 Update prompt body to match Slack format.
- [ ] 8.3 Update Discord approval response handler to mirror Slack's mapping.
- [ ] 8.4 Update Discord resolution message to the single-line format.
- [ ] 8.5 Snapshot tests parallel to Slack.

## 9. Agent guidance (AGENTS.md, tool description, failure path)

- [ ] 9.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` with the new approval flow guidance and the schedule-creation pre-approval suggestion. Bump `metadata.version`.
- [ ] 9.2 Update AGENTS.md (and any other live identity files: `feeds/skills/.system/files/.../AGENTS.md` if present) with the load-bearing `set_working_directory` instruction. Include the consequence framing ("burns the user's attention and your token budget").
- [ ] 9.3 Update the `set_working_directory` tool description in `src/Netclaw.Actors/Tools/SetWorkingDirectoryTool.cs` to read as "declare your project root and expand your trusted scope." Remove any `cd`-style framing.
- [ ] 9.4 Update `ShellTool` failure-result handling so when the deny reason is "cwd outside safe spaces" AND `set_working_directory` is in the audience's tool exposure list, the result includes the one-line hint pointing at `set_working_directory <cwd>`.
- [ ] 9.5 Unit test the failure-path hint: emitted on cwd-outside denial; not emitted on hard-deny refusal; not emitted when `set_working_directory` is unavailable to the audience.

## 10. Schedule-creation flow + evals

- [ ] 10.1 Document the schedule-creation pre-approval pattern in `feeds/skills/.system/files/netclaw-operations/SKILL.md` (covered in 9.1, but cross-check phrasing covers the "ask the user, then call trust-verb" flow).
- [ ] 10.2 Add eval case (positive): session opens with a user prompt that mentions a specific repo path; assert agent calls `set_working_directory <path>` before issuing any shell tool call to that tree.
- [ ] 10.3 Add eval case (negative): session opens with no project signal ("what's 2+2?", "explain X"); assert agent does NOT call `set_working_directory` preemptively.
- [ ] 10.4 Add eval case (recovery): in a session where the agent is denied a shell call because cwd was outside both safe spaces, assert agent reads the failure-path hint and calls `set_working_directory <path>` on its next turn.
- [ ] 10.5 Add eval case (schedule pre-approval): session opens with a user request to schedule an unattended task using a specific verb (e.g. `freshdesk`); assert agent suggests global pre-approval and (on user confirmation) issues the equivalent of `netclaw approvals trust-verb freshdesk` before completing schedule setup.
- [ ] 10.6 Run the eval suite; baseline pass rate documented in PR.

## 11. Spec sync at archive time

- [ ] 11.1 Run `/opsx-verify` to confirm implementation matches change artifacts.
- [ ] 11.2 Run `/opsx-sync` to fold delta specs into `openspec/specs/tool-approval-gates/spec.md`, `openspec/specs/session-cwd/spec.md`, and `openspec/specs/netclaw-cli/spec.md`.
- [ ] 11.3 Run `/opsx-archive` to move the change to `openspec/changes/archive/`.

## Acceptance gates (across all sections)

- [ ] All unit + integration + snapshot tests green.
- [ ] `dotnet slopwatch analyze` reports no new violations.
- [ ] `./scripts/Add-FileHeaders.ps1 -Verify` passes.
- [ ] Eval suite passes (positive + negative + recovery + schedule-preapproval cases).
- [ ] Manual Slack flow: compound command outside safe space → 5-button prompt → click `Always anywhere` → resolution shows "Saved: ... anywhere" → `tool-approvals.json` contains `(verb, null)` entries.
- [ ] Manual Discord flow: same as Slack with `ButtonStyle.Danger` rendering correctly.
- [ ] Manual: `netclaw approvals trust-verb freshdesk` writes the right entry; `list` labels it `freshdesk anywhere`; `revoke "freshdesk anywhere"` removes it.
- [ ] Manual: legacy v1 `tool-approvals.json` quarantines to `.v1.bak` on first read; CLI surfaces the quarantine note.
