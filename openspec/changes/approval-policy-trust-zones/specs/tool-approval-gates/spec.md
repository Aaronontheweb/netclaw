## ADDED Requirements

### Requirement: Three-layer gate evaluation

The system SHALL evaluate every tool invocation through three layers in
order: (1) hard-deny check, (2) zone gate, (3) verb-pattern gate. A tool
SHALL execute silently only when all three layers pass without prompting.
The hard-deny layer SHALL run first and unconditionally. The zone gate
SHALL run only if hard-deny passes. The verb-pattern gate SHALL run only
if the zone gate passes (either silently or via user approval).

The three layers SHALL be independent: an approval at one layer SHALL NOT
imply approval at another. Trust granted to a directory at the zone gate
SHALL NOT grant any verb at the verb-pattern gate; trust granted to a verb
pattern at the verb-pattern gate SHALL NOT grant any directory at the zone
gate.

#### Scenario: All layers silent for read-only verb in trusted zone

- **GIVEN** the audience baseline includes `~/repos/*` as a trusted zone
- **AND** `cat` is on the read-only verb list
- **WHEN** the agent invokes `shell_execute` with command `cat ~/repos/foo/notes.md`
- **THEN** the hard-deny layer passes
- **AND** the zone gate auto-passes (path inside trusted zone)
- **AND** the verb-pattern gate auto-passes (read-only verb in trusted zone)
- **AND** no prompt is rendered

#### Scenario: Mutating verb in trusted zone prompts only verb gate

- **GIVEN** the audience baseline includes `~/repos/*` as a trusted zone
- **AND** the audience has no `git push *` in `verbPatterns`
- **WHEN** the agent invokes `shell_execute` with command `git push origin main`
  and a path arg under `~/repos/foo/`
- **THEN** the hard-deny layer passes
- **AND** the zone gate auto-passes
- **AND** the verb-pattern gate prompts the user

#### Scenario: Untrusted directory and mutating verb produce two sequential prompts

- **GIVEN** the audience has no zone covering `/etc/nginx`
- **AND** the audience has no `cp *` in `verbPatterns`
- **WHEN** the agent invokes `shell_execute` with command `cp /etc/nginx/old.conf /etc/nginx/new.conf`
- **THEN** the hard-deny layer passes
- **AND** the zone gate prompts first (zone prompt)
- **AND** if the user approves, the verb-pattern gate prompts second (verb prompt)
- **AND** if both approve, the command executes

### Requirement: Trust zones store and evaluation

The system SHALL maintain a per-audience `trustedZones` store of directory
glob patterns. The store SHALL be persisted under each audience key in
`~/.netclaw/config/tool-approvals.json`. The zone gate SHALL pass silently
for a path P when P matches any glob in the union of: (a) the audience
baseline read-allowed roots from `netclaw.json`, (b) the persisted
`trustedZones` for that audience, (c) the in-memory session-scope trusted
zones for the current session, AND (d) the immutable `session_dir` for the
current session (which is always trusted).

When a path P does not match any glob in the union, the zone gate SHALL
prompt the user. The prompt SHALL list every untrusted path in the command
in a single batched prompt, not one prompt per path.

The agent SHALL NOT be able to extend `trustedZones` by issuing commands.
Only user prompt clicks (`Trust this directory` with `Session` or `Always`
scope) SHALL extend the store. The `cd` command in a compound SHALL be
parsed for path attribution but SHALL NOT mutate any zone store.

#### Scenario: Path inside audience baseline auto-passes

- **GIVEN** the Personal audience baseline includes `~/repos/*`
- **WHEN** a command operates on `~/repos/foo/file.txt`
- **THEN** the zone gate passes silently for that path

#### Scenario: Path outside all zones prompts

- **GIVEN** the Personal audience baseline includes `~/repos/*` only
- **AND** `trustedZones` is empty
- **WHEN** a command operates on `/etc/hosts`
- **THEN** the zone gate prompts the user with `/etc/hosts` listed

#### Scenario: Multi-path command produces one batched zone prompt

- **GIVEN** a command operates on `/etc/foo` and `/var/lib/bar`, both untrusted
- **WHEN** the zone gate evaluates the command
- **THEN** a single zone prompt is rendered listing both paths
- **AND** the user's choice applies to both paths simultaneously

#### Scenario: Session-scope zone grant ends with the session

- **GIVEN** a user grants `/tmp/scratch` with `Session` scope in session A
- **WHEN** session A terminates
- **AND** session B (same audience) operates on `/tmp/scratch`
- **THEN** the zone gate prompts session B for `/tmp/scratch`

#### Scenario: Always-scope zone grant survives daemon restart

- **GIVEN** a user grants `/etc/nginx` with `Always` scope in the Personal audience
- **AND** the daemon is restarted
- **WHEN** any Personal session operates on `/etc/nginx`
- **THEN** the zone gate passes silently

#### Scenario: Agent cd in compound does not extend trust

- **GIVEN** the audience has no zone covering `/foreign/path`
- **WHEN** the agent invokes `cd /foreign/path && cat file.txt`
- **THEN** `/foreign/path` is attributed as a path the command operates on
- **AND** the zone gate prompts (path outside all trusted zones)
- **AND** no zone is added to `trustedZones` automatically

### Requirement: Verb-pattern store and evaluation

The system SHALL maintain a per-audience `verbPatterns` store of glob-style
verb patterns (e.g., `git push *`, `rm /tmp/*`, `dotnet test *`). The store
SHALL be persisted under each audience key in
`~/.netclaw/config/tool-approvals.json` colocated with `trustedZones`.

The verb-pattern gate SHALL pass silently for a candidate command when
either: (a) the command's verb chain is on the read-only verb list AND every
path the command operates on is inside a trusted zone (per the zone gate),
OR (b) the command matches any glob in the union of the persisted
`verbPatterns` for the audience and the in-memory session-scope verb
patterns for the current session.

When neither condition is met, the verb-pattern gate SHALL prompt the user.
Read-only verbs SHALL NOT auto-pass when any path in the command is outside
trusted zones — outside-zone access always prompts regardless of verb safety.

#### Scenario: Read-only verb in trusted zone auto-passes

- **GIVEN** `cat` is on the read-only verb list
- **AND** the path operated on is inside a trusted zone
- **WHEN** the verb-pattern gate evaluates `cat /home/user/repos/foo/notes.md`
- **THEN** the gate passes silently

#### Scenario: Read-only verb outside trusted zones prompts

- **GIVEN** `cat` is on the read-only verb list
- **AND** the path operated on is outside all trusted zones
- **WHEN** the zone gate has already prompted and the user clicked `Once`
- **AND** the verb-pattern gate then evaluates the command
- **THEN** the verb-pattern gate also prompts the user

#### Scenario: Mutating verb matching persisted glob auto-passes

- **GIVEN** `verbPatterns` contains `git push *`
- **WHEN** the agent invokes `git push origin main`
- **THEN** the verb-pattern gate passes silently

#### Scenario: Mutating verb not matching any glob prompts

- **GIVEN** `verbPatterns` does not contain a glob matching `kubectl apply`
- **WHEN** the agent invokes `kubectl apply -f manifest.yaml`
- **THEN** the verb-pattern gate prompts the user

#### Scenario: Session-scope verb grant ends with the session

- **GIVEN** the user grants `npm install *` with `Session` scope in session A
- **WHEN** session A terminates
- **AND** session B (same audience) invokes `npm install lodash`
- **THEN** the verb-pattern gate prompts session B

#### Scenario: Always-scope verb grant survives daemon restart

- **GIVEN** the user grants `dotnet test *` with `Always` scope
- **AND** the daemon is restarted
- **WHEN** any session in that audience invokes `dotnet test`
- **THEN** the verb-pattern gate passes silently

### Requirement: Two-store per-audience persistence schema

The system SHALL persist approval state in
`~/.netclaw/config/tool-approvals.json` using a per-audience structure
where each audience key contains exactly two fields: `verbPatterns` (array
of glob strings) and `trustedZones` (array of glob strings). The schema
SHALL NOT contain a `version` field; absence of the v2 schema markers
SHALL trigger archival of the existing file and creation of an empty new
schema file.

```json
{
  "personal": {
    "verbPatterns": ["git push *", "dotnet test *"],
    "trustedZones": ["/etc/nginx"]
  },
  "team": {
    "verbPatterns": [],
    "trustedZones": ["/opt/shared"]
  }
}
```

The file SHALL be operator-editable via the `netclaw approvals` CLI. The
daemon SHALL pick up out-of-band edits on the next approval check without
requiring a restart.

When the daemon reads a `tool-approvals.json` file that contains v1 or v2
shape (presence of top-level `version` field, or array of `ApprovalEntry`
records with `verb`/`directory` fields), the file SHALL be archived to
`tool-approvals.json.v2-discarded.bak` and an empty new-schema store SHALL
be returned. No automatic translation of legacy entries SHALL be performed.

#### Scenario: New-schema file loads correctly

- **GIVEN** `tool-approvals.json` contains a per-audience structure with `verbPatterns` and `trustedZones`
- **WHEN** the daemon loads the file
- **THEN** the in-memory store reflects the file contents

#### Scenario: v2 file archived on first read

- **GIVEN** `tool-approvals.json` contains `"version": 2` and an `ApprovalEntry` array
- **WHEN** the daemon loads the file
- **THEN** the file is moved to `tool-approvals.json.v2-discarded.bak`
- **AND** the in-memory store is empty
- **AND** no v2 entries are translated

#### Scenario: Operator revoke visible without restart

- **GIVEN** the daemon is running with `verbPatterns` containing `git push *`
- **WHEN** an operator removes that entry via `netclaw approvals revoke`
- **AND** a new verb-pattern gate evaluation runs for `git push`
- **THEN** the daemon re-reads the file and observes the entry is gone
- **AND** the user is prompted

### Requirement: In-memory session-scope grants on LlmSessionActor

The system SHALL maintain in-memory session-scope grants for each session
on the `LlmSessionActor` instance. Two segments SHALL exist:
`SessionTrustedZones` (list of directory globs) and `SessionVerbPatterns`
(list of verb glob strings). These SHALL be populated when the user clicks
the `Session` button on either gate's prompt.

Session-scope grants SHALL NOT be persisted to disk. They SHALL be lost on
session termination (channel disconnection, daemon restart, actor recovery
from snapshot). `SessionSnapshot` SHALL NOT include session-scope grants.

#### Scenario: Session-scope zone applies for the rest of the session

- **GIVEN** a user clicks `Session` on a zone prompt for `/tmp/scratch`
- **WHEN** the agent makes another call operating under `/tmp/scratch`
  in the same session
- **THEN** the zone gate passes silently

#### Scenario: Session-scope verb pattern applies for the rest of the session

- **GIVEN** a user clicks `Session` on a verb prompt for `npm install *`
- **WHEN** the agent invokes `npm install` again in the same session
- **THEN** the verb-pattern gate passes silently

#### Scenario: Session-scope grants do not survive daemon restart

- **GIVEN** session A has session-scope zone `/tmp/scratch`
- **WHEN** the daemon restarts
- **AND** session A is recovered from snapshot
- **THEN** the recovered session has no session-scope grants
- **AND** subsequent operations on `/tmp/scratch` re-prompt

#### Scenario: Session-scope grants are not in SessionSnapshot

- **GIVEN** a session with session-scope grants populated
- **WHEN** a snapshot is taken
- **THEN** the snapshot serialization omits the session-scope segments

### Requirement: Sequential approval workflow per call

The system SHALL coordinate per-call approval through a
`ToolApprovalWorkflow` value type on the `LlmSessionActor`. The workflow
SHALL transition through stages `Start → ZoneGate → VerbGate → Complete`,
issuing at most one zone prompt and at most one verb prompt per call. The
workflow SHALL execute the gates in order and SHALL only advance to the
verb gate after the zone gate completes (silently or via user response).

The mid-turn approval pause (`TaskCompletionSource`-based block on the
tool execution task) SHALL remain in place for the duration of the entire
workflow, spanning both prompts when both fire. Other tool calls in the
same batch SHALL execute in parallel and SHALL NOT block on this
workflow.

If the user denies at any prompt, the workflow SHALL terminate with
`Denied` and the tool task SHALL unblock with the denial result. If the
configured approval timeout (default: 5 minutes) elapses on either
prompt, the workflow SHALL terminate with `TimedOut`.

#### Scenario: Zone-then-verb sequential prompts

- **GIVEN** a call needs both gates
- **WHEN** the workflow runs
- **THEN** the zone prompt is sent first
- **AND** the verb prompt is sent only after the user responds to the zone prompt
- **AND** the user responds to two prompts in sequence

#### Scenario: Deny on zone prompt skips verb prompt

- **GIVEN** the zone gate is prompting the user
- **WHEN** the user clicks `Deny`
- **THEN** the workflow terminates with `Denied`
- **AND** the verb prompt is never rendered

#### Scenario: Concurrent calls each have their own workflow

- **GIVEN** two tool calls are dispatched in the same batch, both needing approval
- **WHEN** both workflows run
- **THEN** each call has its own `ToolApprovalWorkflow` instance
- **AND** prompt responses are routed to the correct workflow by `CallId`

#### Scenario: Other tool calls in batch are not blocked

- **GIVEN** a batch containing `web_search`, `shell_execute` (needs approval),
  and `file_read`
- **WHEN** the batch executes
- **THEN** `web_search` and `file_read` execute in parallel immediately
- **AND** `shell_execute` blocks waiting for the workflow to complete

### Requirement: Sequential 4-button approval prompts

When the zone gate prompts the user, the prompt SHALL render exactly four
buttons in one row: `Once`, `Session`, `Trust <directory>`, `Deny`. The
buttons `Trust <directory>` and `Deny` SHALL be styled per their effect
(`Trust` as primary action; `Deny` as danger). The header text SHALL ask
*"Allow `<audience>` to operate inside `<paths>`?"* listing one or more
untrusted paths.

When the verb-pattern gate prompts the user, the prompt SHALL render
exactly four buttons in one row: `Once`, `Session`, `Always <verb-pattern>`,
`Deny`. The header text SHALL ask *"Allow `<verb-pattern>` to run?"* where
`<verb-pattern>` is the glob (e.g., `git push *`).

All button labels SHALL fit within Slack's 76-character and Discord's
80-character button-text caps. When the displayed entity (path or verb
pattern) would exceed the cap, the label SHALL truncate with an ellipsis
and the full value SHALL appear in the prompt body.

Both prompts SHALL include the audience name and command context in the
body so the user can scan the decision context without inferring it.

#### Scenario: Zone prompt shows path and 4-button row

- **GIVEN** a command operates on the untrusted path `/etc/nginx`
- **WHEN** the zone prompt is rendered
- **THEN** the header reads "Allow Personal to operate inside /etc/nginx?"
- **AND** the action row contains `Once`, `Session`, `Trust /etc/nginx`, `Deny`

#### Scenario: Verb prompt shows pattern and 4-button row

- **GIVEN** a command `git push origin main` and no matching `verbPatterns`
- **WHEN** the verb prompt is rendered
- **THEN** the header reads "Allow `git push *` to run?"
- **AND** the action row contains `Once`, `Session`, `Always git push *`, `Deny`

#### Scenario: Multi-path zone prompt batches paths

- **GIVEN** a command operates on `/etc/nginx` and `/var/log`, both untrusted
- **WHEN** the zone prompt is rendered
- **THEN** the body lists both paths
- **AND** a single 4-button row applies to both

#### Scenario: Long path label truncates with full value in body

- **GIVEN** an untrusted path that exceeds the button-label cap
- **WHEN** the zone prompt is rendered
- **THEN** the `Trust ...` button label is truncated with an ellipsis
- **AND** the full path appears in the prompt body

### Requirement: Resolution message format for two-store schema

After an approval response is processed, the channel SHALL render a
single-line resolution message identifying which store was extended and
the scope. Permitted formats:

- `Saved zone: <path>` — for `Always` on the zone prompt.
- `Saved zone (this session): <path>` — for `Session` on the zone prompt.
- `Saved verb: <pattern>` — for `Always` on the verb prompt.
- `Saved verb (this session): <pattern>` — for `Session` on the verb prompt.
- `Approved (no save)` — for `Once`.
- `Denied` — for `Deny`.

The message SHALL replace the previous `Saved: <verb-list> in <directory>`
format. No `Patterns` or `Directory Roots` headers SHALL appear.

#### Scenario: Always on zone prompt produces zone-saved message

- **GIVEN** the user clicks `Trust /etc/nginx` (Always scope) on a zone prompt
- **WHEN** the resolution message is rendered
- **THEN** the message reads `Saved zone: /etc/nginx`

#### Scenario: Session on verb prompt produces session-saved message

- **GIVEN** the user clicks `Session` on a verb prompt for `npm install *`
- **WHEN** the resolution message is rendered
- **THEN** the message reads `Saved verb (this session): npm install *`

#### Scenario: Once produces approved-no-save message

- **GIVEN** the user clicks `Once` on either prompt
- **WHEN** the resolution message is rendered
- **THEN** the message reads `Approved (no save)`

### Requirement: ShellSyntaxTree dependency for command parsing

The system SHALL consume the `ShellSyntaxTree` NuGet package
(`github.com/Aaronontheweb/ShellSyntaxTree`) for all shell command
parsing. The matcher SHALL feed the raw command string to
`IShellParser.Parse(string)` and operate exclusively on the returned
`ParsedCommand` AST. The matcher SHALL NOT contain regex-based
tokenization, per-verb path-extraction tables, or quote/escape handling
— those concerns belong to the parser library.

The matcher SHALL extract from the AST: (a) per-clause verb chains for
the verb-pattern gate, (b) per-clause path arguments (resolved against
the spawn cwd) for the zone gate, (c) cd-in-compound directory
attribution (paths attributed to subsequent commands within the same
compound), (d) redirect target paths, and (e) bash-c inner command
recursion.

When the parser flags any path token as dynamic (unresolved env var,
unresolved expansion), the matcher SHALL skip that token rather than
extracting a literal value. Extraction failure for a clause SHALL cause
the clause to be treated as path-arg-less; the verb gate still applies.

#### Scenario: Matcher consumes ShellSyntaxTree AST

- **GIVEN** a command `git -C /repo log`
- **WHEN** the matcher processes it
- **THEN** it calls `IShellParser.Parse("git -C /repo log")`
- **AND** uses the returned `ParsedCommand` to extract verb chain `git log`
  and path `/repo`

#### Scenario: cd-in-compound propagates paths to subsequent clauses

- **GIVEN** a command `cd /target && cmd1 file.txt && cmd2 other.txt`
- **WHEN** the matcher processes it
- **THEN** `/target` is attributed as a path each of `cmd1` and `cmd2` operates on
- **AND** zones are checked for `/target` in addition to any explicit path args

#### Scenario: Dynamic path tokens are skipped

- **GIVEN** a command `rm $UNRESOLVED_VAR/foo`
- **WHEN** the matcher processes it
- **THEN** the dynamic token is skipped (no literal `$UNRESOLVED_VAR/foo` extracted)
- **AND** the clause is treated as path-arg-less for the zone gate
- **AND** the verb gate still evaluates `rm`

#### Scenario: bash -c wrapper recurses into inner command

- **GIVEN** a command `bash -c "git push --force"`
- **WHEN** the matcher processes it
- **THEN** the inner command is parsed
- **AND** verb chain `git push` is extracted from the inner command

## MODIFIED Requirements

### Requirement: Tool approval configuration per audience

The system SHALL support per-audience tool approval configuration via
`ToolApprovalConfig` on `ToolAudienceProfile`. Each audience profile SHALL
independently specify a `DefaultMode` (Auto, Approval, Deny) and per-tool
overrides in `ToolOverrides`. The default `DefaultMode` SHALL be `Auto` (no
approval required). Runtime audience defaults SHALL NOT implicitly place
`shell_execute` in `Approval` mode. Instead, the init-generated Personal config
SHALL explicitly write
`ApprovalPolicy.ToolOverrides.shell_execute = Approval` as the recommended
shell-safe default.

When `shell_execute` is in Approval mode, the three-layer gate (hard-deny,
zone, verb-pattern) SHALL evaluate each invocation. The audience's baseline
trusted zones SHALL be derived from the audience trust profile's
`read_allowed_roots`; the in-memory session-scope grants SHALL apply for
the current session only.

#### Scenario: Shell requires approval in init-generated Personal config

- **GIVEN** a Personal audience session whose generated config explicitly sets
  `ApprovalPolicy.ToolOverrides.shell_execute` to `Approval`
- **WHEN** the agent invokes `shell_execute`
- **THEN** `ToolAccessPolicy` marks the call as approval-gated
- **AND** the three-layer gate evaluates the call
- **AND** if any layer requires user input, an approval prompt is emitted

#### Scenario: Tool in Auto mode executes without approval

- **GIVEN** a tool whose approval mode is `Auto` for the session's audience
- **WHEN** the agent invokes the tool
- **THEN** the tool executes immediately without an approval prompt

#### Scenario: Tool in Deny mode is always blocked

- **GIVEN** a tool whose approval mode is `Deny` for the session's audience
- **WHEN** the agent invokes the tool
- **THEN** the tool is denied with reason `tool_denied_by_approval_policy`
- **AND** no approval prompt is offered

#### Scenario: Per-audience independence

- **GIVEN** Personal sets `shell_execute` to `Approval` and Team sets it to `Deny`
- **WHEN** a Personal session invokes `shell_execute`
- **THEN** `ToolAccessPolicy` marks the call as approval-gated
- **AND** the three-layer gate evaluates against Personal's stores
- **AND** when a Team session invokes `shell_execute`
- **THEN** the system denies immediately without prompting

### Requirement: Configurable hard deny list

The system SHALL enforce a configurable hard deny list of command patterns that
are blocked before the zone gate or verb-pattern gate is consulted. Denied
commands SHALL never be approvable. The system SHALL ship with sensible defaults:
commands that kill the Netclaw daemon process, `rm -rf /`, `rm -rf ~/`, and fork
bombs. Operators SHALL be able to add or remove patterns via configuration.

The hard deny list SHALL operate on the parsed `ParsedCommand` AST clauses
provided by `ShellSyntaxTree`. Each clause SHALL be checked independently;
a compound containing any hard-denied clause SHALL be denied wholesale.

#### Scenario: Hard-denied command blocked before any gate

- **GIVEN** a command matching the hard deny list (e.g., `netclaw daemon stop`)
- **WHEN** the agent invokes `shell_execute` with that command
- **THEN** the command is denied with reason `hard_deny_self_destructive`
- **AND** no zone or verb prompt is offered
- **AND** the denial is logged

#### Scenario: Hard deny enforced even in HostAllowed mode

- **GIVEN** `ShellMode` is `HostAllowed` (no approval config)
- **WHEN** the agent runs a hard-denied command
- **THEN** the command is still blocked

#### Scenario: Operator adds custom hard deny pattern

- **GIVEN** the operator adds `docker rm` to the hard deny list in config
- **WHEN** the agent runs `docker rm my-container`
- **THEN** the command is denied

#### Scenario: Compound command with hard-denied clause

- **GIVEN** a compound command `git add . && netclaw daemon stop`
- **WHEN** the agent invokes `shell_execute`
- **THEN** the entire command is denied because one clause matches hard deny

### Requirement: IToolApprovalMatcher extension point

The system SHALL define an `IToolApprovalMatcher` interface for tool-specific
extraction and gate evaluation. Shell SHALL implement zone-and-verb
evaluation using `ShellSyntaxTree`. A default implementation SHALL provide
tool-name-level matching for tools without a custom matcher.

The shell matcher SHALL accept the parsed `ParsedCommand` AST plus the
audience's trust state (baseline zones, persisted zones, session zones,
persisted verb patterns, session verb patterns) and return per-clause
evaluation results indicating which gate(s) require user prompting.

#### Scenario: Shell matcher consumes ParsedCommand and trust state

- **GIVEN** a parsed `npm install lodash` and the Personal audience trust state
- **WHEN** the matcher evaluates the call
- **THEN** the matcher returns the gate state (zone-pass, verb-pass / verb-prompt etc.)
  for each clause

#### Scenario: Default matcher used for tools without custom matcher

- **GIVEN** a tool without a custom `IToolApprovalMatcher` implementation
- **WHEN** the matcher evaluates the call
- **THEN** the default tool-name-level matcher is used
- **AND** the zone gate is not evaluated (no command paths to check)

### Requirement: Mid-turn approval pause

The system SHALL pause individual tool execution tasks when approval is required
without blocking other tool calls in the same batch. The pause SHALL use a
`TaskCompletionSource` that completes when the session actor receives an approval
response. The pause SHALL span the entire `ToolApprovalWorkflow` (both prompts
when both fire). A configurable timeout (default: 5 minutes) SHALL auto-deny if
no response arrives on either prompt.

#### Scenario: Approval-pending tool blocks while others complete

- **GIVEN** a batch of 3 tool calls: `web_search`, `shell_execute`, `file_read`
- **AND** `shell_execute` requires approval
- **WHEN** the batch executes
- **THEN** `web_search` and `file_read` execute in parallel immediately
- **AND** `shell_execute` blocks waiting for the approval workflow
- **AND** the session actor remains responsive to messages

#### Scenario: Approval timeout auto-denies on either prompt

- **GIVEN** a zone prompt has been emitted
- **WHEN** no response arrives within the configured timeout
- **THEN** the workflow terminates with `TimedOut`
- **AND** the tool task unblocks
- **AND** the tool result says "Approval timed out after X seconds"

#### Scenario: Approved tool executes after both prompts complete

- **GIVEN** a tool is in the verb-pattern gate after zone-gate approval
- **WHEN** the user approves the verb prompt
- **THEN** the tool executes and returns its result
- **AND** any persisted grants are written to `tool-approvals.json`

#### Scenario: Denied tool returns denial message

- **GIVEN** a tool is blocked at either prompt
- **WHEN** the user denies
- **THEN** the workflow terminates with `Denied`
- **AND** the tool returns "Command denied by user" as the tool result
- **AND** no command is executed

### Requirement: ToolInteractionRequest/Response protocol

The system SHALL define a `ToolInteractionRequest` session output and
`ToolInteractionResponse` session command for channel-mediated approval
interactions. The interaction `Kind` SHALL identify the interaction type
and the gate that issued it: `approval_zone` for the zone gate prompt,
`approval_verb` for the verb-pattern gate prompt. `ToolInteractionRequest`
SHALL be a lifecycle output (always delivered regardless of `OutputFilter`).

`ToolInteractionRequest` for `approval_zone` SHALL include a `Paths` field
containing the untrusted paths the prompt asks about. `ToolInteractionRequest`
for `approval_verb` SHALL include a `VerbPattern` field containing the
glob pattern proposed for the `Always` button.

`ToolInteractionResponse` SHALL include `CallId`, the gate the response
applies to, and the selected scope (`Once`, `Session`, `Always`, `Deny`).

#### Scenario: Zone prompt emitted as session output

- **GIVEN** the zone gate decides to prompt
- **WHEN** the workflow issues the prompt
- **THEN** a `ToolInteractionRequest` with `Kind=approval_zone` is emitted
- **AND** it includes `CallId`, `ToolName`, the untrusted paths, and the audience name

#### Scenario: Verb prompt emitted as session output

- **GIVEN** the verb-pattern gate decides to prompt
- **WHEN** the workflow issues the prompt
- **THEN** a `ToolInteractionRequest` with `Kind=approval_verb` is emitted
- **AND** it includes `CallId`, `ToolName`, the verb pattern, and the audience name

#### Scenario: Channel routes response to the correct workflow stage

- **GIVEN** a workflow has emitted a zone prompt
- **WHEN** the user submits a response
- **THEN** the channel sends a `ToolInteractionResponse` to the session actor
- **AND** the workflow advances to the verb gate stage on Approve
- **OR** terminates with Denied

### Requirement: Channel approval capability

Channels SHALL declare whether they support interactive approval via a
capability flag. When a tool requires approval and the active channel does NOT
support it, the system SHALL immediately deny the tool with reason
`channel_does_not_support_approval`. The system SHALL NOT hang or timeout.

The capability check SHALL apply once per call regardless of how many
prompts the workflow would issue — a non-interactive channel cannot
serve any prompt.

#### Scenario: Unsupported channel auto-denies

- **GIVEN** the headless channel (no interactive user)
- **AND** `shell_execute` is in Approval mode
- **WHEN** the agent invokes `shell_execute`
- **THEN** the tool is immediately denied with
  `channel_does_not_support_approval`

#### Scenario: Supported channel renders approval prompt

- **GIVEN** the Slack channel (supports interactive approval)
- **AND** `shell_execute` is in Approval mode
- **WHEN** the agent invokes `shell_execute` requiring zone or verb approval
- **THEN** the channel renders the appropriate prompt (zone or verb)

## REMOVED Requirements

### Requirement: Shell command pattern matching
**Reason:** Verb-chain extraction logic is delegated to `ShellSyntaxTree`.
The Netclaw matcher no longer owns tokenization, compound splitting, or
`bash -c` recursion — those concerns moved to the parser library. The
matcher consumes the parsed AST instead.
**Migration:** Replace `ShellTokenizer.SplitCompoundCommand` /
`Tokenize` callers with `IShellParser.Parse` and walk the
`ParsedCommand.Clauses`. See ADDED requirement "ShellSyntaxTree
dependency for command parsing."

### Requirement: Persistent approval storage
**Reason:** The `(verb, directory)` `ApprovalEntry` schema is replaced by
the two-store per-audience schema with `verbPatterns` and `trustedZones`
glob arrays. The `version: 2` marker and v1 quarantine logic are
obsolete; v1 and v2 file shapes both archive to a `.v2-discarded.bak`
sibling on first read.
**Migration:** No data migration. Operators on pre-release Netclaw will
see their existing `tool-approvals.json` archived and an empty
new-schema file created. They re-grant via prompts as needed. See ADDED
requirement "Two-store per-audience persistence schema."

### Requirement: Directory-root approvals for shell_execute
**Reason:** The `(verb, directory)` cross-product entry shape is gone.
Trust geography (zones) and trust action (verb patterns) are now two
independent stores, each evaluated by its own gate.
**Migration:** A user wanting v2's `(grep, ~/.netclaw/logs/)` behavior
now grants `~/.netclaw/logs/` to `trustedZones` (silent for read-only
verbs), or both `~/.netclaw/logs/` to `trustedZones` AND `grep *` to
`verbPatterns` (silent for any verb). See ADDED requirements
"Trust zones store and evaluation" and "Verb-pattern store and evaluation."

### Requirement: Safe-verb auto-allow short-circuit in declared safe spaces
**Reason:** The "safe-verbs in safe-spaces" short-circuit is replaced by
the explicit zone gate. Read-only verbs auto-pass *only* inside trusted
zones; outside-zone access always prompts. The audience-aware safe-space
roots are now resolved from `trustedZones` ∪ baseline ∪ `session_dir`
∪ in-memory session-scope zones, not from a separate
`ToolAudienceProfileResolver` pathway.
**Migration:** The shipped `safe-verbs.linux.json` /
`safe-verbs.windows.json` files become the read-only verb list consulted
by the verb-pattern gate when deciding whether to auto-pass for paths
inside trusted zones. The list itself survives; the gate logic
consuming it changes. The `ScopedShellSafeVerbPolicy` class is
replaced by gate evaluation in the new matcher.

### Requirement: Five-button approval prompt with verb-and-directory framing
**Reason:** The 5-button row encoded the v2 `(verb, directory)`
cross-product (`Always here` vs `Always anywhere`). The new model has
two independent gates, each with a 4-button row of `[Once / Session /
Always / Deny]`. Sequential prompting replaces the cross-product
button matrix.
**Migration:** Slack and Discord prompt builders SHALL render two
distinct prompt shapes (zone-gate, verb-gate) each with a 4-button row,
issued sequentially. See ADDED requirement "Sequential 4-button approval
prompts."

### Requirement: Resolution message single-line format
**Reason:** The `Saved: <verb-list> in <directory>` format encodes the
v2 cross-product shape. The new format identifies which store was
extended (zone vs verb) and the scope.
**Migration:** Update channel adapters to emit the new resolution
message format. See ADDED requirement "Resolution message format for
two-store schema."

### Requirement: Pattern extraction refuses bash control-flow
**Reason:** Bash control-flow detection (for/while/case) is now part of
`ShellSyntaxTree`'s parser. When the parser cannot produce a clean AST
(unparseable input, unbalanced quotes, control-flow), the matcher
SHALL still fall back to offering only `Once` and `Deny`, but the
detection logic itself lives in the parser library.
**Migration:** Replace `ShellTokenizer.SplitCompoundCommand` callers
with `IShellParser.Parse`; the parser surfaces an unparseable flag that
the matcher consumes to constrain the prompt buttons. See ADDED
requirement "ShellSyntaxTree dependency for command parsing."
