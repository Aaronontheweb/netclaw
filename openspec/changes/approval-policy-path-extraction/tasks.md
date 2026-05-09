# Approval Policy Path Extraction — Tasks

This change is small enough to ship as a single PR; no PR-split is needed.
Reference: proposal.md (why), design.md (how), specs/tool-approval-gates/spec.md (what).

## 1. Path classification + verb-only extraction

- [ ] 1.1 Add a `IsPathToken(string)` predicate to `ShellTokenizer` that
  returns true when the token starts with `/`, `~/`, `./`, `../`, or is
  exactly `~`, `.`, `..`. Pure-string check; no filesystem syscalls.
- [ ] 1.2 Update `ShellTokenizer.SplitCompoundCommand` (or whichever
  per-clause tokenizer the matcher uses) to emit a typed result per
  clause: `(verb: string, candidateDirectory: string?)`. The verb is
  the chain of leading non-flag, non-path tokens. The candidate
  directory is the first path-like token in the clause, or null.
- [ ] 1.3 Update `IToolApprovalMatcher.ExtractCandidateVerbs` (or its
  shell-specific equivalent) to return verbs only. Add a parallel
  method `ExtractCandidateDirectories` returning the per-clause
  directories aligned by index, OR change the return type to
  `IReadOnlyList<(string Verb, string? Directory)>` — design choice
  documented in the implementation comment.
- [ ] 1.4 Apply file-vs-directory parent inference at extraction time:
  if `Path.HasExtension(candidateDirectory)` is true, persist
  `Path.GetDirectoryName(candidateDirectory)` instead. String
  operation only, no syscalls.
- [ ] 1.5 Unit tests for tokenizer: absolute path, tilde-prefixed, dot
  relative, dot-dot relative, URL (negative), internal-slash regex
  literal (negative), command with no path argument, multi-path
  command (first wins).
- [ ] 1.6 Unit tests for the file-parent rule: `cat ~/.bashrc` →
  parent is `~/`; `find /home/petabridge` → unchanged (no extension).

## 2. Matcher uses effective directory

- [ ] 2.1 Update `ApprovalPatternMatching.MatchesShellApproval` to take
  `(candidateVerb, candidateDirectory, cwd, approvedEntries)` and
  match using `effectiveDirectory = candidateDirectory ?? cwd`.
- [ ] 2.2 Resolve relative `effectiveDirectory` (`./build`, `../shared`)
  against cwd before the under-check.
- [ ] 2.3 Apply existing symlink-segment guard to the resolved
  effective directory along its full path.
- [ ] 2.4 Update `ToolAccessPolicy.CheckApprovalGate` call sites that
  feed the matcher to thread `candidateDirectory` through alongside
  the verb chain.
- [ ] 2.5 Unit tests for matcher: candidate's extracted path is under
  entry directory → approve; candidate's extracted path is sibling
  → reject; candidate has no path, cwd is under entry directory →
  approve; candidate has no path, cwd is outside → reject; entry
  directory is null → approve regardless.
- [ ] 2.6 Unit test for the folder-scoped trust compounding scenario
  from the spec: entry `(find, /home/petabridge)` matches candidate
  `find /home/petabridge/.netclaw -name X`.

## 3. Persistence on Always here uses effective directory

- [ ] 3.1 In `LlmSessionActor`'s approval-response handler, when
  `decision == ApprovedAlways` and `pending.CandidateVerbs` is the
  pre-change shape, extend it to also carry per-candidate directories
  so the persistence loop writes `(verb, candidateDirectory ?? cwd)`
  per clause.
- [ ] 3.2 Apply the shallow-path guard to the effective directory
  (not just cwd): if a candidate's effective directory fails
  `IsCwdTooShallow`, skip persistence for that candidate and emit a
  one-line note in the resolution message.
- [ ] 3.3 Unit/integration test: clicking `Always here` on
  `find /home/petabridge -name X` writes
  `(find, /home/petabridge)`, NOT `(find /home/petabridge, cwd)` and
  NOT `(find, cwd)`.
- [ ] 3.4 Integration test: clicking `Always here` on
  `cat ~/.bashrc` writes `(cat, ~/)` (parent of file), and a future
  `cat ~/.profile` is auto-approved.

## 4. Side-effect skip list

- [ ] 4.1 Add a `SideEffectVerbs` const list to
  `ApprovalPatternMatching` (or a sibling helper): `echo`, `printf`,
  `:`, `true`, `false`. Conservative — stdout-only verbs with no
  filesystem or process effect when used without redirects.
- [ ] 4.2 Add `IsPureSideEffect(verb, hasPath, hasRedirect)` helper:
  returns true when the verb is in the skip list AND there is no
  path argument AND no shell redirect operator (`>`, `>>`, `|` —
  pipe in compound matters because the right side may consume the
  output usefully).
- [ ] 4.3 In the `LlmSessionActor` persistence loop, skip
  `IsPureSideEffect` candidates entirely. The decision still
  authorizes them for the current call (no extra runtime gating
  needed); only persistence is suppressed.
- [ ] 4.4 Update the resolution-line builder
  (`SlackApprovalBlockBuilder.BuildResolutionLine` and
  `DiscordApprovalPromptBuilder` equivalent) to distinguish
  "Saved: <verbs>" from "Authorized for this call: <verbs>" so the
  operator can see what ended up in the store vs what didn't.
- [ ] 4.5 Unit tests: `cat A.txt; echo "==="; cat B.txt` with
  Always here persists only the `cat` entries (one or two depending
  on path-collapse rule); `echo X > /tmp/log` with Always here
  persists `(echo, /tmp/)` because of the redirect target.

## 5. Agent guidance and resolution-line copy

- [ ] 5.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  Approval Prompts section to reflect implicit-directory-from-path-args.
  Bump `metadata.version` to 2.1.0. Tone: "shell commands with a path
  argument declare scope automatically; `set_working_directory` is the
  fallback when commands won't carry a path."
- [ ] 5.2 Update `src/Netclaw.Configuration/Resources/AGENTS.md`
  "Declare Your Project Root Early" section. Soften the "FIRST
  shell-related action MUST be" imperative — for verb-with-path
  commands, the act of running the command IS the declaration.
  Keep the imperative for sessions where the agent is doing
  multiple shell calls in cwd-less form.
- [ ] 5.3 Update `SetWorkingDirectoryTool` description: keep "declare
  your project root and expand your trusted scope" framing, add a
  short note that path arguments to shell commands also expand
  scope automatically.

## 6. Tests + eval cases

- [ ] 6.1 Run the existing `Approval Policy v2` eval cases (see
  `evals/run-evals.sh`); the positive case `approval_set_working_
  directory_positive` is now expected to pass via implicit scope OR
  via explicit `set_working_directory` — either path satisfies the
  assertion. No assertion change needed.
- [ ] 6.2 Add an eval case `approval_path_compounding`: session opens,
  user mentions a project at `/some/path`, agent runs `ls /some/path`
  (gets prompt, clicks Always here), then runs
  `ls /some/path/subdir` — assert no second prompt.
  (Deferred — runs against the same flaky local provider that
  blocked the v2 eval baseline; can be authored without running.)
- [ ] 6.3 Add a unit test for the side-effect skip list end-to-end:
  approve a multi-clause command including `echo`, verify the store
  contains entries for the action verbs but not for `echo`.

## 7. Spec sync at archive time

- [ ] 7.1 Run `/opsx-verify` to confirm implementation matches change
  artifacts.
- [ ] 7.2 Run `/opsx-sync` to fold the delta spec into
  `openspec/specs/tool-approval-gates/spec.md`.
- [ ] 7.3 Run `/opsx-archive` to move the change to
  `openspec/changes/archive/`.

## Acceptance gates

- [ ] All unit + integration tests green.
- [ ] `dotnet slopwatch analyze` reports no new violations.
- [ ] `./scripts/Add-FileHeaders.ps1 -Verify` passes.
- [ ] Manual binary-swap validation in a real Slack session:
  `find /repo` → click Always here → `find /repo/sub` auto-runs
  with no prompt; `tool-approvals.json` contains
  `(find, /repo)`, NOT `(find /repo, ...)`.
- [ ] Manual: clicking Always here on a multi-clause command with
  `echo` produces a store with action-verb entries only, no echo
  entry.
- [ ] Resolution line distinguishes "Saved" from "Authorized for
  this call" so operators can see what was suppressed.
