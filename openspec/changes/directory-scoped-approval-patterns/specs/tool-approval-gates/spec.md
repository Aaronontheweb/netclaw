## ADDED Requirements

### Requirement: Directory-scoped approval patterns

The system SHALL support directory-scoped approval patterns for shell commands
targeting path-aware verbs. When the user selects "Approve for this chat" (B) or
"Approve always" (C) for a shell command that targets a recognizable file path,
the system SHALL store a directory-scoped pattern (e.g., `grep /home/.netclaw/logs/`)
instead of the exact file-path pattern. "Approve once" (A) SHALL continue to use
exact patterns.

When a recognizable path operand is relative, the system SHALL resolve it
against the shell tool `WorkingDirectory` before extracting either exact or
directory-scoped approval patterns. Existing operands that already denote a
directory SHALL preserve that directory scope instead of widening to the parent.

A trailing `/` on a stored pattern SHALL signal directory scope. The system SHALL
use `PathUtility.IsWithinRoot()` for boundary-safe containment matching — not
naive string prefix comparison.

The system SHALL enforce a minimum directory depth of 2 path segments below root.
Patterns targeting root-level directories (`/`, `/home/`, `/etc/`, `/tmp/`) SHALL
be rejected, falling back to exact-pattern behavior.

Directory-scoped approvals SHALL be verb-isolated: an approval for
`cat /home/.netclaw/logs/` SHALL NOT match `grep /home/.netclaw/logs/`.

#### Scenario: Directory-scoped pattern stored on Approve For This Chat

- **GIVEN** a shell command `cat /home/.netclaw/logs/crash-foo.log` requires approval
- **WHEN** the user selects "Approve for this chat"
- **THEN** the session-scoped approval stores `cat /home/.netclaw/logs/`
- **AND** a subsequent `cat /home/.netclaw/logs/daemon.log` in the same session
  does not prompt

#### Scenario: Directory-scoped pattern stored on Approve Always

- **GIVEN** a shell command `grep -l "timeout" /home/.netclaw/logs/daemon.log`
  requires approval
- **WHEN** the user selects "Approve always"
- **THEN** `grep /home/.netclaw/logs/` is written to `tool-approvals.json`
- **AND** future sessions auto-approve grep commands targeting files under
  `/home/.netclaw/logs/`

#### Scenario: Relative path resolves against working directory

- **GIVEN** the shell tool `WorkingDirectory` is `/workspace/project`
- **AND** the command is `cat logs/app.log`
- **WHEN** exact and directory-scoped approval patterns are extracted
- **THEN** the exact pattern is `cat /workspace/project/logs/app.log`
- **AND** the directory-scoped pattern is `cat /workspace/project/logs/`

#### Scenario: Existing directory operand preserves its scope

- **GIVEN** the shell tool `WorkingDirectory` is `/workspace/project`
- **AND** the command is `find logs -name '*.log'`
- **WHEN** directory-scoped approval extraction runs
- **THEN** the extracted pattern is `find /workspace/project/logs/`
- **AND** the scope is not widened to `/workspace/project/`

#### Scenario: Approve Once uses exact pattern

- **GIVEN** a shell command `cat /home/.netclaw/logs/crash.log` requires approval
- **WHEN** the user selects "Approve once"
- **THEN** only the current blocked call is retried
- **AND** a subsequent `cat /home/.netclaw/logs/other.log` prompts again

#### Scenario: Directory scope does not cross verb boundaries

- **GIVEN** `cat /home/.netclaw/logs/` is approved
- **WHEN** the agent runs `grep "error" /home/.netclaw/logs/app.log`
- **THEN** the command still requires approval (verb mismatch)

#### Scenario: Nested files match directory scope

- **GIVEN** `ls /home/.netclaw/` is approved
- **WHEN** the agent runs `ls /home/.netclaw/logs/deep/nested/file.txt`
- **THEN** the command is auto-approved (path is within approved directory)

#### Scenario: Sibling directory does not match

- **GIVEN** `cat /home/.netclaw/logs/` is approved
- **WHEN** the agent runs `cat /home/.netclaw/config/netclaw.json`
- **THEN** the command requires approval (different directory)
- **AND** `ToolPathPolicy` independently blocks the protected path at execution time

#### Scenario: Shallow directory scope rejected

- **GIVEN** a shell command `cat /etc/passwd` requires approval
- **WHEN** directory scope extraction runs
- **THEN** the parent directory `/etc/` has only 1 segment (below minimum of 2)
- **AND** the system falls back to exact-pattern behavior

#### Scenario: Boundary-safe path matching prevents prefix collisions

- **GIVEN** `cat /home/user/` is approved
- **WHEN** the agent runs `cat /home/usersecret/data.txt`
- **THEN** the command requires approval
- **AND** `PathUtility.IsWithinRoot` prevents the false positive

### Requirement: Directory pattern extraction via IToolApprovalMatcher

`IToolApprovalMatcher` SHALL define an `ExtractDirectoryPatterns()` method that
returns directory-scoped patterns for a tool invocation. `ShellApprovalMatcher`
SHALL implement this by scanning all non-flag arguments for the first
recognizable path operand, resolving relative paths against `WorkingDirectory`,
expanding home directory tokens, extracting the scoped directory, normalizing
the path, and enforcing minimum depth. For compound commands and `bash -c`
wrappers, each segment SHALL be processed recursively. When no directory scope
is available for a segment, the segment's exact approval pattern SHALL be used
as fallback.

`DefaultApprovalMatcher` and `FilePathApprovalMatcher` SHALL return empty lists.

#### Scenario: grep extracts path from second argument

- **GIVEN** the command `grep -l "timeout" /home/.netclaw/logs/daemon.log`
- **WHEN** `ExtractDirectoryPatterns` runs
- **THEN** the pattern `grep /home/.netclaw/logs/` is extracted
- **AND** the search term `"timeout"` is skipped (not a path)

#### Scenario: grep exact pattern uses normalized path operand

- **GIVEN** the shell tool `WorkingDirectory` is `/workspace/project`
- **AND** the command is `grep -l "timeout" logs/daemon.log`
- **WHEN** exact approval pattern extraction runs
- **THEN** the pattern is `grep /workspace/project/logs/daemon.log`
- **AND** the search term `"timeout"` is not used as the exact operand

#### Scenario: Compound command extracts patterns per segment

- **GIVEN** the command `cat /home/.netclaw/logs/crash.log && grep "error" /var/log/syslog`
- **WHEN** `ExtractDirectoryPatterns` runs
- **THEN** `cat /home/.netclaw/logs/` is extracted for the first segment
- **AND** the second segment falls back to its verb chain (depth too shallow)

#### Scenario: Pipe segment can contribute direct directory scope

- **GIVEN** the shell tool `WorkingDirectory` is `/workspace/project`
- **AND** the command is `cat logs/app.log | jq .message`
- **WHEN** `ExtractDirectoryPatterns` runs
- **THEN** `cat /workspace/project/logs/` is extracted for the direct path-aware segment
- **AND** the `jq` segment does not gain directory scope from piped input alone

#### Scenario: Indirect path flow is not inferred for MVP

- **GIVEN** the command is `find logs -name '*.log' | xargs grep timeout`
- **WHEN** `ExtractDirectoryPatterns` runs
- **THEN** the `find` segment may contribute `find <resolved>/logs/`
- **AND** the downstream `grep` segment does not inherit that directory scope via `xargs`

#### Scenario: Glob paths use parent directory

- **GIVEN** the command `ls /home/.netclaw/logs/crash-*.log`
- **WHEN** `ExtractDirectoryPatterns` runs
- **THEN** the pattern `ls /home/.netclaw/logs/` is extracted
- **AND** the glob component is stripped

### Requirement: Dynamic approval option labels

When directory patterns are available, the system SHALL customize the approval
option labels to show the directory scope only when the full approval set for
the request maps cleanly to a single directory scope. The labels SHALL follow the format:
- B: `"Approve in {directory} for this chat"`
- C: `"Approve in {directory} always"`

Options A ("Approve once") and D ("Deny") SHALL retain their default labels.

#### Scenario: Labels show directory scope for path-aware commands

- **GIVEN** a shell command `grep "error" /home/.netclaw/logs/app.log`
  requires approval
- **WHEN** the approval prompt is generated
- **THEN** option B reads "Approve in /home/.netclaw/logs/ for this chat"
- **AND** option C reads "Approve in /home/.netclaw/logs/ always"

#### Scenario: Labels use defaults when no directory scope

- **GIVEN** a shell command `git push origin main` requires approval
- **WHEN** the approval prompt is generated
- **THEN** option B reads the default "Approve for this chat"
- **AND** option C reads the default "Approve always"

#### Scenario: Labels stay generic for mixed approval sets

- **GIVEN** a shell command `cat /home/.netclaw/logs/crash.log && git push origin main`
  requires approval
- **WHEN** the approval prompt is generated
- **THEN** option B reads the default "Approve for this chat"
- **AND** option C reads the default "Approve always"
- **AND** no partial directory-specific label is shown for the whole request

## MODIFIED Requirements

### Requirement: ToolInteractionRequest/Response protocol

The system SHALL define a `ToolInteractionRequest` session output and
`ToolInteractionResponse` session command for channel-mediated approval
interactions.
The interaction `Kind` SHALL identify the interaction type (`approval` for v1).
`ToolInteractionRequest` SHALL be a lifecycle output (always delivered regardless
of `OutputFilter`).

`ToolInteractionRequest` SHALL include a `DirectoryPatterns` field containing
directory-scoped patterns extracted from the tool invocation. When non-empty and
the user selects "Approve for this chat" or "Approve always", the session actor
SHALL record the directory patterns instead of the exact file-path patterns.

#### Scenario: Approval request emitted as session output

- **GIVEN** a tool requires approval
- **WHEN** the pipeline detects the approval requirement
- **THEN** a `ToolInteractionRequest` with `Kind=approval` is emitted
- **AND** it includes `CallId`, `ToolName`, the command/pattern, and available
  options (approve once, approve for this chat, approve always, deny)

#### Scenario: Approval request includes directory patterns

- **GIVEN** a shell command targets a file under `/home/.netclaw/logs/`
- **WHEN** the approval request is generated
- **THEN** `ToolInteractionRequest.DirectoryPatterns` contains the directory-scoped
  pattern (e.g., `cat /home/.netclaw/logs/`)
- **AND** `ToolInteractionRequest.Patterns` contains the exact file-path pattern

#### Scenario: Channel routes response back to session

- **GIVEN** a `ToolInteractionRequest` has been emitted
- **WHEN** the user selects an option (for MVP Slack, via text reply)
- **THEN** the channel sends a `ToolInteractionResponse` to the session actor
- **AND** the response includes `CallId` and the selected option key

### Requirement: Persistent approval storage

The system SHALL store persistent approvals ("Approve Always" decisions) in
`~/.netclaw/config/tool-approvals.json`, separate from `netclaw.json`. The file
SHALL NOT be monitored by `ConfigWatcherService`. The file SHALL contain
per-audience sections with per-tool approval lists. For the shipped MVP shell
flow, the lists SHALL contain command patterns, including directory-scoped
patterns (trailing `/`). Approval lookup and recording
SHALL be mediated by `IToolApprovalService`.

#### Scenario: Approve Always persists directory pattern to file

- **GIVEN** the user clicks "Approve Always" for a command targeting
  `/home/.netclaw/logs/crash.log`
- **WHEN** the approval is processed
- **THEN** `cat /home/.netclaw/logs/` is added to the Personal shell_execute list
  in `tool-approvals.json`
- **AND** the daemon does NOT restart

#### Scenario: Persistent approvals loaded at startup

- **GIVEN** `tool-approvals.json` contains
  `{"personal":{"shell_execute":["git push", "cat /home/.netclaw/logs/"]}}`
- **WHEN** the daemon starts
- **THEN** `git push` is pre-approved for Personal audience shell commands
- **AND** `cat` commands targeting files under `/home/.netclaw/logs/` are pre-approved

#### Scenario: Approve Once is retry-scoped only

- **GIVEN** the user clicks "Approve Once" for pattern `docker build`
- **WHEN** the approval is processed
- **THEN** the blocked `docker build` call is retried immediately
- **AND** a later `docker build` call in the same session prompts again
- **AND** `tool-approvals.json` is NOT modified

#### Scenario: Approve For This Chat stores directory pattern in session

- **GIVEN** the user clicks "Approve For This Chat" for a command targeting
  `/home/.netclaw/logs/daemon.log`
- **WHEN** the approval is processed
- **THEN** the directory-scoped pattern is approved for the current session only
- **AND** `tool-approvals.json` is NOT modified
- **AND** a new session will prompt again

### Requirement: Shell command pattern matching

The system SHALL extract verb-chain prefix patterns from shell commands using
tokenization. The verb chain SHALL consist of non-flag tokens from the start of
the command until the first flag (`-`), path, or URL argument. For compound
commands (`&&`, `||`, `;`, `|`), each segment SHALL be evaluated independently.
For `bash -c` or `sh -c` wrappers, the inner command SHALL be extracted and
scanned recursively.

The system SHALL support directory-scoped pattern matching. When an approved
pattern ends with `/`, the system SHALL match any candidate pattern with the same
verb whose path argument is within the approved directory, using
`PathUtility.IsWithinRoot()` for boundary-safe containment.

For path-aware verbs with a recognizable path operand, exact approval pattern
extraction SHALL use the normalized path operand instead of the raw verb chain.

#### Scenario: Verb chain extracted from simple command

- **GIVEN** the command `git push origin main`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `git push`

#### Scenario: Verb chain stops at flag

- **GIVEN** the command `ls -la /tmp`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `ls /tmp`

#### Scenario: Multi-level verb chain

- **GIVEN** the command `docker compose up -d`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `docker compose up`

#### Scenario: Compound command segments evaluated independently

- **GIVEN** the command `git add . && git commit -m "fix" && git push`
- **WHEN** approval is checked
- **THEN** patterns `git add`, `git commit`, and `git push` are each checked
  independently against the approval state surfaced through `IToolApprovalService`

#### Scenario: Unapproved compound segments batched in one prompt

- **GIVEN** `git add` is approved but `git commit` and `git push` are not
- **WHEN** the command `git add . && git commit -m "fix" && git push` is checked
- **THEN** a single approval prompt lists both `git commit` and `git push`
- **AND** the full compound command is shown for context

#### Scenario: bash -c inner command scanned recursively

- **GIVEN** the command `bash -c "git push --force"`
- **WHEN** approval and hard deny are checked
- **THEN** the inner command `git push --force` is extracted and scanned
- **AND** pattern `git push` is checked through `IToolApprovalService`

#### Scenario: Directory-scoped approved pattern matches file within directory

- **GIVEN** `cat /home/.netclaw/logs/` is in the approved patterns
- **WHEN** the candidate pattern `cat /home/.netclaw/logs/crash.log` is checked
- **THEN** `ApprovalPatternMatching.MatchesAny` returns true

#### Scenario: Windows-native shell support is tracked separately

- **GIVEN** this MVP change targets the current shell approval pipeline
- **WHEN** native Windows shell path semantics are considered
- **THEN** they are out of scope for this change
- **AND** follow-up work is tracked separately in issue #899
