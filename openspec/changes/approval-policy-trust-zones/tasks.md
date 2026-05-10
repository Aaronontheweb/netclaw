## 1. ShellSyntaxTree library (external repo)

- [ ] 1.1 Create `github.com/Aaronontheweb/ShellSyntaxTree` repo with .NET 10 class library project, MIT license, `ShellSyntaxTree` NuGet package id, README, CONTRIBUTING. Verify: empty project builds with `dotnet build`.
- [ ] 1.2 Define core AST records: `ParsedCommand`, `Clause`, `VerbChain`, `Arg` (with `Kind = Literal | EnvVar | Glob | Tilde | DynamicSkip`), `Redirect`, `CompoundOperator` enum. Verify: types compile and have round-trip equality semantics covered by unit tests.
- [ ] 1.3 Define `IShellParser` interface with `ParsedCommand Parse(string command)` and `bool TryParse(string, out ParsedCommand)`. Verify: interface compiles, default implementation throws NotImplementedException.
- [ ] 1.4 Implement `BashLexer`: tokenization with single/double-quote handling, escape sequences, redirect operators, compound operators (`&&`, `||`, `;`, `|`), subshell parens, here-doc start markers. Verify: lexer test suite covers each token kind with positive and negative cases (≥40 cases).
- [ ] 1.5 Implement `BashParser`: recursive-descent over the lexer's token stream producing `ParsedCommand`. Handle clause splitting on compound operators, subshell isolation, `bash -c "inner"` recursion, here-doc body skipping, unparseable-input flag. Verify: parser test suite covers each grammar production with ≥80 cases including counter-examples.
- [ ] 1.6 Implement `BashArity` dictionary: per-verb token count for verb chains (`cd: 1`, `git: 2`, `docker compose: 3`, `bun run: 3`, etc.). Sourced from OpenCode plus Netclaw-observed verbs. Verify: arity lookup unit-tested with ≥30 verbs.
- [ ] 1.7 Implement `FILES`/`CWD`/`CMD_FILES` verb tables for path-arg extraction. Verify: per-verb path-arg extraction unit-tested for each table with ≥40 cases.
- [ ] 1.8 Implement per-verb `pathArgs` filter: knows `chmod 755 file` (first arg is mode, rest are paths), `cp -r src dst` (skip flag, both rest are paths), Windows-cmd `/X` flag skipping. Verify: unit-tested per verb (≥20 verbs).
- [ ] 1.9 Implement `Resolver`: `~` expansion, `$VAR` / `${VAR}` env-var resolution against an injected `IEnvironmentSnapshot`, glob detection (`*`, `?`, `[`), `filesystem::/path` prefix stripping, dynamic-skip when expansion fails. Verify: resolver unit tests cover each expansion case with ≥30 inputs.
- [ ] 1.10 Implement cd-in-compound propagation: walk `Clauses`; when first clause is `cd /target`, attribute `/target` as an additional path on subsequent clauses within the same compound (until subshell boundary). Verify: AST-level test ≥10 cases including nested subshells.
- [ ] 1.11 Author corpus at `tests/Corpus/` with input + expected-AST JSON files. Seed from sanitized real-agent emissions (PII stripped). Target ≥100 corpus entries covering each grammar production and counter-examples. Verify: corpus runner test executes every entry and asserts AST equality.
- [ ] 1.12 Set up CI on the external repo: `dotnet test` on push, NuGet publish on tag. Verify: CI green on initial commit; tag-based publish documented.
- [ ] 1.13 Cut `0.1.0-alpha` tag and verify NuGet publish pipeline produces a consumable package. Verify: package downloadable from nuget.org and installable in a separate test project.

## 2. Netclaw consumption of ShellSyntaxTree

- [ ] 2.1 Add `<ProjectReference>` to sibling `ShellSyntaxTree` clone in `Netclaw.Security.csproj` for parallel development. Verify: `dotnet build` succeeds with sibling reference.
- [ ] 2.2 Register `BashParser` as `IShellParser` in `Netclaw.Security` DI registration extension. Verify: DI resolution unit-tested.
- [ ] 2.3 Replace `ShellTokenizer.Tokenize` / `SplitCompoundCommand` callers with `IShellParser.Parse` calls returning `ParsedCommand`. Files affected: `ShellCommandPolicy.cs`, `ShellApprovalSemantics.cs`, `ToolPathPolicy.cs`. Verify: all callers compile and pass existing tests after migration.
- [ ] 2.4 Delete `ShellTokenizer.cs` and `ShellTokenizerTests.cs` once all callers migrated. Verify: no references remain via `grep -r ShellTokenizer src/`.
- [ ] 2.5 Once ShellSyntaxTree v0.1 publishes to NuGet, switch `<ProjectReference>` to `<PackageReference>` with version pin. Update `Directory.Packages.props`. Verify: `dotnet restore` succeeds against the package; sibling repo path no longer required.

## 3. Two-store persistence schema

- [ ] 3.1 Define new `TrustZoneStore` and `VerbPatternStore` records (per-audience glob arrays). Verify: serialization round-trip tested (System.Text.Json AOT-source-generated).
- [ ] 3.2 Update `tool-approvals.json` JSON schema to per-audience structure with `verbPatterns` and `trustedZones` array fields. Update the schema file at `src/Netclaw.Configuration/Schemas/` (per CLAUDE.md schema sync rule) if `tool-approvals.json` has a schema entry. Verify: schema validates a sample new-shape file and rejects v1/v2 shapes.
- [ ] 3.3 Implement `ToolApprovalStore.LoadAsync` that detects v1/v2 shapes (top-level `version` field, `ApprovalEntry` arrays) and archives to `.v2-discarded.bak`, returning empty new-shape store. Verify: unit tests cover v1, v2, new-shape, and missing-file inputs.
- [ ] 3.4 Implement `ToolApprovalStore.PersistAsync` writing per-audience structure with atomic file replace. Verify: unit-tested for concurrent writers (last-write-wins, no truncation).
- [ ] 3.5 Implement `ToolApprovalStore.AddZone(audience, glob, scope)` and `AddVerbPattern(audience, glob, scope)` methods. Scope `Always` writes to disk; `Session` is a no-op at this layer (handled by LlmSessionActor). Verify: unit-tested.
- [ ] 3.6 Implement `ToolApprovalStore.RevokeZone(audience, glob)` and `RevokeVerbPattern(audience, glob)` methods. Verify: unit-tested.
- [ ] 3.7 Wire out-of-band file edit detection: re-read file on each gate evaluation (or via `ConfigWatcherService` if performance becomes an issue). Verify: integration test edits the file mid-test and confirms next evaluation sees the change.

## 4. In-memory session-scope grants

- [ ] 4.1 Add `SessionTrustedZones` and `SessionVerbPatterns` fields to `LlmSessionActor` (in-memory `List<string>` each). Verify: fields initialize empty on actor start.
- [ ] 4.2 Add `LlmSessionActor.AddSessionZone(glob)` and `AddSessionVerbPattern(glob)` methods invoked by the workflow when user clicks `Session` scope. Verify: unit-tested.
- [ ] 4.3 Confirm `SessionSnapshot` does NOT include session-scope grants. Add explicit test asserting snapshot serialization omits these fields. Verify: snapshot round-trip test.
- [ ] 4.4 Confirm actor recovery from snapshot does NOT restore session-scope grants. Verify: recovery test asserts both lists are empty after restore.

## 5. Three-layer gate evaluator

- [ ] 5.1 Define `GateEvaluator` service with `Evaluate(ParsedCommand, Audience, TrustState)` returning per-clause `GateResult` (hard-deny / zone-pass / zone-prompt / verb-pass / verb-prompt / verb-pass-readonly). Verify: unit-tested for each result kind.
- [ ] 5.2 Implement Layer 1 hard-deny check on parsed clauses. Reuse existing hard-deny patterns; refactor the matcher to consume `Clause` instead of token strings. Verify: hard-deny unit tests pass against parsed-clause inputs.
- [ ] 5.3 Implement Layer 2 zone gate: extract paths per clause from AST (path args + cd-in-compound attribution + redirect targets); check each against the union (audience baseline ∪ persisted zones ∪ session zones ∪ session_dir); collect untrusted paths into a single batched prompt. Verify: per-path zone evaluation unit-tested with ≥30 cases.
- [ ] 5.4 Implement Layer 3 verb-pattern gate: extract verb chain per clause via BashArity; check against persisted patterns ∪ session patterns. Read-only verb auto-pass conditional on all clause paths being inside trusted zones. Verify: gate decision unit-tested for read-only-in-zone, read-only-outside-zone, mutating-with-pattern, mutating-without-pattern.
- [ ] 5.5 Define `IToolApprovalMatcher` v3 interface accepting `ParsedCommand` + `TrustState` and returning per-clause `GateResult`. Implement `ShellApprovalMatcher` for shell tools; default tool-name matcher for non-shell tools. Verify: matcher unit-tested.
- [ ] 5.6 Wire `GateEvaluator` into `ToolAccessPolicy`. Replace v2 matcher integration. Verify: `ToolAccessPolicy` unit tests updated and passing.

## 6. Per-call ToolApprovalWorkflow

- [ ] 6.1 Define `ToolApprovalWorkflow` value type on `LlmSessionActor` with fields `Call`, `Paths`, `ZoneState`, `VerbState`, `Stage`. Verify: type compiles with minimal allocation footprint.
- [ ] 6.2 Implement workflow state machine `Start → ZoneGate → VerbGate → Complete`. Issue zone prompt when `Paths` contain untrusted entries; advance to verb gate after response; issue verb prompt when verb-pattern gate decides to prompt. Verify: state-machine unit tests cover all transition paths.
- [ ] 6.3 Implement workflow termination on `Deny` at any stage; on `Approved` at `Complete` stage; on `TimedOut` at any stage. Verify: termination unit tests for each cause.
- [ ] 6.4 Wire workflow into `LlmSessionActor` per-call dispatch. Replace v2 single-prompt code path. Verify: integration test exercises a call requiring both prompts.
- [ ] 6.5 Confirm watchdog pause/resume spans the entire workflow (across both prompts). Verify: watchdog test asserts no pre-emption between prompts.
- [ ] 6.6 Apply scope handling: `Once` runs no persistence; `Session` calls `AddSessionZone`/`AddSessionVerbPattern`; `Always` calls `ToolApprovalStore.AddZone`/`AddVerbPattern`. Verify: scope-handling unit tests.

## 7. Slack adapter

- [ ] 7.1 Update `SlackApprovalBlockBuilder` to render two distinct prompt shapes by `Kind`: `approval_zone` (header asks about paths; row contains `Once / Session / Trust <path> / Deny`) and `approval_verb` (header asks about verb pattern; row contains `Once / Session / Always <pattern> / Deny`). Verify: builder unit tests assert block structure for each Kind.
- [ ] 7.2 Implement label truncation when path/pattern exceeds 76-character button-text cap. Full value remains in body. Verify: truncation unit test for ≥10 long inputs.
- [ ] 7.3 Update Slack interaction handler to route response by `Kind` to the correct `ToolApprovalWorkflow` stage. Verify: handler test routes zone vs verb responses correctly.
- [ ] 7.4 Update resolution message rendering: `Saved zone: ...`, `Saved verb: ...`, `Approved (no save)`, `Denied`. Verify: resolution-line unit tests.
- [ ] 7.5 Update Slack sample fixtures and snapshot tests for the new prompt shapes. Verify: snapshot tests pass after baseline update.

## 8. Discord adapter

- [ ] 8.1 Update `DiscordApprovalPromptBuilder` to mirror Slack's two-prompt-shape rendering with Discord button styles. Verify: builder unit tests for each Kind.
- [ ] 8.2 Implement label truncation at 80-character cap. Verify: truncation test.
- [ ] 8.3 Update Discord interaction handler for `Kind`-based routing. Verify: handler test.
- [ ] 8.4 Update resolution message rendering for Discord. Verify: resolution-line tests.

## 9. CLI / TUI

- [ ] 9.1 Update `netclaw approvals` TUI to surface two tabs `[Z]ones` and `[V]erbs` per audience. Verify: TUI integration test renders both tabs.
- [ ] 9.2 Update `netclaw approvals list` (non-interactive) to print both stores per audience. Verify: CLI snapshot test.
- [ ] 9.3 Update `netclaw approvals revoke` to accept a glob and an axis (`--zone` or `--verb`). Verify: CLI command unit test.
- [ ] 9.4 Update `netclaw approvals trust-verb <verb-pattern>` to accept the new glob format (`git push *`, `dotnet test *`). Verify: CLI command test.
- [ ] 9.5 Add `netclaw approvals trust-zone <directory>` for adding a zone via CLI (matching the prompt's `Trust this directory` action). Verify: CLI command test.
- [ ] 9.6 Update `netclaw approvals --help` text with new schema and commands. Verify: help text snapshot test.

## 10. Deletions

- [ ] 10.1 Delete `src/Netclaw.Actors/Tools/SetWorkingDirectoryTool.cs` and remove all references from tool registries, audience profiles, tool exposure lists. Verify: `grep -r SetWorkingDirectory src/` returns zero matches outside this change's spec deltas.
- [ ] 10.2 Delete `WorkingContext.ProjectDirectory` field and all reads/writes. Update `WorkingContext.cs`, `SessionSnapshot` serialization, `WorkingContext.ToContextBlock()`. Verify: `grep -r ProjectDirectory src/` returns zero matches outside spec deltas.
- [ ] 10.3 Delete the `[project-instructions]` slot from `SystemPromptAssembler.Assemble()` and the `FileSystemPromptProvider` project handling. Verify: system prompt snapshot tests updated; no project-content slot remains.
- [ ] 10.4 Delete the daemon-side identity-file-loading code path (the `.netclaw/AGENTS.md` / `AGENTS.md` / `CLAUDE.md` / `CONTEXT.md` lookup driven by ProjectDirectory). Verify: code path tests removed; agent-side discipline test added in §11.
- [ ] 10.5 Delete `ScopedShellSafeVerbPolicy` (replaced by inline gate evaluation). Verify: no callers remain; safe-verbs file load logic survives but is consumed directly by the new gate evaluator.
- [ ] 10.6 Update `WorkingContext.ResolveShellCwd` to remove `ProjectDirectory` from the resolution chain (now: `args.WorkingDirectory → SessionDirectory`). Verify: cwd-resolution unit tests pass.
- [ ] 10.7 Delete the `ShellTool` failure-path hint emission for `set_working_directory`. Verify: hint emission tests removed.

## 11. Agent guidance

- [ ] 11.1 Update `src/Netclaw.Configuration/Resources/AGENTS.md` with explicit project-context lookup discipline section (".netclaw/AGENTS.md → AGENTS.md → CLAUDE.md → CONTEXT.md, read once per project per session via file_read; don't re-read on later turns"). Verify: AGENTS.md content review; eval suite case asserts agent reads project context on first project entry.
- [ ] 11.2 Remove any text in `Resources/AGENTS.md` referencing `set_working_directory` as the gesture for declaring project scope. Verify: `grep set_working_directory src/Netclaw.Configuration/Resources/` returns zero.
- [ ] 11.3 Add an "Approval gates" section to `Resources/AGENTS.md` describing the two-gate model (zone + verb), the `Once / Session / Always / Deny` button row, and how to phrase rationale for prompts the user will see. Verify: AGENTS.md review.
- [ ] 11.4 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` (per CLAUDE.md system skills sync rule) for the new gate model, two-store schema, and CLI changes. Bump skill `metadata.version`. Verify: skill content review; `dotnet test` covering skill-loading passes.

## 12. Eval suite

- [ ] 12.1 Add eval case: read-only verb in trusted zone runs silently (zero prompts). Verify: eval green.
- [ ] 12.2 Add eval case: mutating verb in trusted zone produces exactly one (verb) prompt. Verify: eval green.
- [ ] 12.3 Add eval case: untrusted directory + mutating verb produces exactly two sequential prompts. Verify: eval green.
- [ ] 12.4 Add eval case: multi-path command (cp /src /dst, both untrusted) produces one batched zone prompt listing both paths. Verify: eval green.
- [ ] 12.5 Add eval case: `cd /target && cmd` attributes /target as a path the cmd operates on. Verify: eval green.
- [ ] 12.6 Add eval case: agent reads project identity file via file_read on first project operation. Verify: eval green.
- [ ] 12.7 Add eval case: agent does NOT call `set_working_directory` (tool not exposed). Verify: eval green.
- [ ] 12.8 Run full eval suite (`./evals/run-evals.sh`); confirm no regressions. Verify: all evals green.

## 13. Documentation

- [ ] 13.1 Update `docs/help/approvals.md` (or equivalent operator-facing doc) with the two-gate model, four-button row, two-store schema, and TUI Z/V tabs. Verify: doc review.
- [ ] 13.2 Update `docs/spec/` engineering specs that referenced v2 approval shape. Verify: `grep -r "ApprovalEntry\|verb.*directory.*tuple" docs/spec/` returns matches only in archive/historical.
- [ ] 13.3 Add release note draft to `RELEASE_NOTES.md` covering: BREAKING approval schema change, BREAKING removal of `set_working_directory`, new ShellSyntaxTree dependency. Verify: release note review.
- [ ] 13.4 Update `PROJECT_CONTEXT.md` and `TOOLING.md` if they reference the old approval model or `set_working_directory`. Verify: greps pass.

## 14. Quality gates

- [ ] 14.1 Run `dotnet slopwatch analyze`; baseline any unavoidable new entries with justification. Verify: no new violations or all baselined.
- [ ] 14.2 Run `./scripts/Add-FileHeaders.ps1 -Verify`; confirm all `.cs` files have copyright headers. Verify: zero missing headers.
- [ ] 14.3 Run `dotnet test` across the solution; confirm all tests pass after the rewrite. Verify: green.
- [ ] 14.4 Run `openspec verify --change approval-policy-trust-zones`; confirm artifacts and implementation align. Verify: verify exits 0.
- [ ] 14.5 Manual binary-swap on Aaron's machine: replace running daemon, confirm sample sessions exercise both gates with expected prompt shapes on Slack. Verify: Aaron sign-off.
- [ ] 14.6 Confirm `tool-approvals.json` schema synced (per CLAUDE.md Configuration Schema Sync Rule) if any `*Config` types changed. Verify: `ConfigSchemaDoctorCheck` passes at runtime.
