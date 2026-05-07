## ADDED Requirements

### Requirement: Directory-root approvals for shell_execute

For `shell_execute`, `Approve once` SHALL remain exact blocked-call retry only.
It SHALL NOT create a reusable session approval, persistent approval, or
directory-root approval.

For `shell_execute`, when the user selects `Approve for this chat` (B) or
`Approve always` (C) and the shell approval unit contains one or more
recognized local filesystem paths, the system SHALL store directory roots for
that approval unit instead of verb-specific or command-pattern-specific shell
approvals.

Directory approvals SHALL be root-based and verb-agnostic. A later shell
approval unit SHALL be auto-approved only when every recognized local
filesystem path in that unit resolves under already approved roots.

If a shell approval unit yields no reusable local directory roots, directory
approval SHALL NOT apply and the system SHALL fall back to exact approval
behavior for that unit.

The system SHALL enforce minimum directory depth, path normalization,
boundary-safe containment, path traversal checks, and `ToolPathPolicy` as the
safety backstop for directory-root approvals.

#### Scenario: Approve once retries only the blocked call

- **GIVEN** a shell command `cat /home/.netclaw/logs/crash.log` requires approval
- **WHEN** the user selects `Approve once`
- **THEN** only the current blocked call is retried
- **AND** no reusable approval is recorded
- **AND** a later `cat /home/.netclaw/logs/other.log` prompts again

#### Scenario: Approve for this chat stores a reusable directory root

- **GIVEN** a shell command `cat /home/.netclaw/logs/crash-foo.log` requires approval
- **WHEN** the user selects `Approve for this chat`
- **THEN** the session-scoped approval stores the directory root `/home/.netclaw/logs/`
- **AND** a later `grep "error" /home/.netclaw/logs/daemon.log` in the same session
  does not prompt

#### Scenario: Approve always stores a reusable directory root

- **GIVEN** a shell command `grep -l "timeout" /home/.netclaw/logs/daemon.log`
  requires approval
- **WHEN** the user selects `Approve always`
- **THEN** `/home/.netclaw/logs/` is written to `tool-approvals.json` for
  `shell_execute`
- **AND** a future-session `ls /home/.netclaw/logs/archive.log` is auto-approved

#### Scenario: All recognized local paths in a unit must be covered

- **GIVEN** `/home/.netclaw/logs/` is approved for `shell_execute`
- **WHEN** the agent runs `cat /home/.netclaw/logs/app.log /home/.netclaw/config/netclaw.json`
- **THEN** the command still requires approval because not all recognized local
  filesystem paths fall under approved roots

#### Scenario: No reusable local roots falls back to exact approval behavior

- **GIVEN** a shell command `git push origin main` requires approval
- **WHEN** the user selects `Approve for this chat`
- **THEN** no directory root is stored
- **AND** the system falls back to exact approval behavior for `git push`

#### Scenario: Shallow directory root falls back to exact approval behavior

- **GIVEN** a shell command `cat /etc/passwd` requires approval
- **WHEN** directory-root extraction runs
- **THEN** the derived root `/etc/` is rejected as too shallow
- **AND** the system falls back to exact approval behavior

#### Scenario: Boundary-safe matching prevents prefix collisions

- **GIVEN** `/home/user/` is approved for `shell_execute`
- **WHEN** the agent runs `cat /home/usersecret/data.txt`
- **THEN** the command requires approval
- **AND** `PathUtility.IsWithinRoot` prevents the false positive

### Requirement: Directory root extraction via IToolApprovalMatcher

`IToolApprovalMatcher` SHALL define an `ExtractDirectoryRoots()` method that
returns reusable directory roots for a tool invocation.

For `shell_execute`, extraction SHALL operate on shell approval units. Units
SHALL split on `&&`, `||`, and `;`. Pipelines joined by `|` SHALL stay inside
the same approval unit.

`ShellApprovalMatcher` SHALL scan each approval unit for recognized local
filesystem paths, expand and normalize them, derive reusable parent directory
roots, and enforce minimum depth and path-safety checks. For `bash -c` or
`sh -c` wrappers, the inner command SHALL be extracted and scanned recursively.

`DefaultApprovalMatcher` and `FilePathApprovalMatcher` SHALL return empty lists.

#### Scenario: grep extracts a root from a later argument

- **GIVEN** the command `grep -l "timeout" /home/.netclaw/logs/daemon.log`
- **WHEN** `ExtractDirectoryRoots` runs
- **THEN** the root `/home/.netclaw/logs/` is extracted
- **AND** the search term `"timeout"` is ignored

#### Scenario: Pipeline stays in one approval unit

- **GIVEN** the command `grep "error" /home/.netclaw/logs/app.log | wc -l`
- **WHEN** `ExtractDirectoryRoots` runs
- **THEN** the pipeline is treated as one approval unit
- **AND** the root `/home/.netclaw/logs/` is extracted for that unit

#### Scenario: Control operators split approval units

- **GIVEN** the command `cat /home/.netclaw/logs/app.log && cat /home/.netclaw/config/netclaw.json`
- **WHEN** `ExtractDirectoryRoots` runs
- **THEN** the `&&` creates two approval units
- **AND** each unit is evaluated independently for reusable roots

#### Scenario: Glob paths use parent directory root

- **GIVEN** the command `ls /home/.netclaw/logs/crash-*.log`
- **WHEN** `ExtractDirectoryRoots` runs
- **THEN** the root `/home/.netclaw/logs/` is extracted
- **AND** the glob component does not become part of the stored root

### Requirement: Dynamic approval option labels

When directory roots are available, the system SHALL customize the approval
option labels to show the reusable root scope. The labels SHALL follow the
format:
- B: `"Approve in {directory-root} for this chat"`
- C: `"Approve in {directory-root} always"`

Options A ("Approve once") and D ("Deny") SHALL retain their default labels.

#### Scenario: Labels show reusable root scope for shell commands

- **GIVEN** a shell command `grep "error" /home/.netclaw/logs/app.log`
  requires approval
- **WHEN** the approval prompt is generated
- **THEN** option B reads `Approve in /home/.netclaw/logs/ for this chat`
- **AND** option C reads `Approve in /home/.netclaw/logs/ always`

#### Scenario: Labels use defaults when no reusable directory root exists

- **GIVEN** a shell command `git push origin main` requires approval
- **WHEN** the approval prompt is generated
- **THEN** option B reads the default "Approve for this chat"
- **AND** option C reads the default "Approve always"

## MODIFIED Requirements

### Requirement: ToolInteractionRequest/Response protocol

The system SHALL define a `ToolInteractionRequest` session output and
`ToolInteractionResponse` session command for channel-mediated approval
interactions.
The interaction `Kind` SHALL identify the interaction type (`approval` for v1).
`ToolInteractionRequest` SHALL be a lifecycle output (always delivered regardless
of `OutputFilter`).

`ToolInteractionRequest` SHALL include a `DirectoryRoots` field containing
reusable directory roots extracted from the tool invocation. When non-empty and
the user selects `Approve for this chat` or `Approve always`, the session actor
SHALL record the directory roots instead of exact shell approval patterns.

#### Scenario: Approval request emitted as session output

- **GIVEN** a tool requires approval
- **WHEN** the pipeline detects the approval requirement
- **THEN** a `ToolInteractionRequest` with `Kind=approval` is emitted
- **AND** it includes `CallId`, `ToolName`, the command/pattern, and available
  options (approve once, approve for this chat, approve always, deny)

#### Scenario: Approval request includes directory roots

- **GIVEN** a shell command targets a file under `/home/.netclaw/logs/`
- **WHEN** the approval request is generated
- **THEN** `ToolInteractionRequest.DirectoryRoots` contains `/home/.netclaw/logs/`
- **AND** the request still includes the exact blocked approval pattern for retry

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
flow, the lists SHALL contain exact approvals and directory roots as applicable.
Approval lookup and recording SHALL be mediated by `IToolApprovalService`.

#### Scenario: Approve always persists directory root to file

- **GIVEN** the user clicks "Approve Always" for a command targeting
  `/home/.netclaw/logs/crash.log`
- **WHEN** the approval is processed
- **THEN** `/home/.netclaw/logs/` is added to the Personal `shell_execute` list
  in `tool-approvals.json`
- **AND** the daemon does NOT restart

#### Scenario: Persistent approvals loaded at startup

- **GIVEN** `tool-approvals.json` contains
  `{"personal":{"shell_execute":["git push", "/home/.netclaw/logs/"]}}`
- **WHEN** the daemon starts
- **THEN** `git push` is pre-approved for Personal audience shell commands
- **AND** later shell approval units whose recognized local paths all stay under
  `/home/.netclaw/logs/` are pre-approved

#### Scenario: Approve once is retry-scoped only

- **GIVEN** the user clicks "Approve Once" for pattern `docker build`
- **WHEN** the approval is processed
- **THEN** the blocked `docker build` call is retried immediately
- **AND** a later `docker build` call in the same session prompts again
- **AND** `tool-approvals.json` is NOT modified

#### Scenario: Approve for this chat stores directory root in session

- **GIVEN** the user clicks "Approve For This Chat" for a command targeting
  `/home/.netclaw/logs/daemon.log`
- **WHEN** the approval is processed
- **THEN** the directory root is approved for the current session only
- **AND** `tool-approvals.json` is NOT modified
- **AND** a new session will prompt again

### Requirement: Shell command pattern matching

The system SHALL extract verb-chain prefix patterns from shell commands using
tokenization. The verb chain SHALL consist of non-flag tokens from the start of
the command until the first flag (`-`), path, or URL argument. For shell
approval units, `&&`, `||`, and `;` SHALL split into separate units, while `|`
SHALL remain inside the current unit.
For `bash -c` or `sh -c` wrappers, the inner command SHALL be extracted and
scanned recursively.

When a shell approval unit has no reusable directory roots, the system SHALL use
exact approval behavior for that unit.

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

#### Scenario: Control operators create separate approval units

- **GIVEN** the command `git add . && git commit -m "fix" && git push`
- **WHEN** approval is checked
- **THEN** `git add`, `git commit`, and `git push` are checked as separate
  approval units against the approval state surfaced through
  `IToolApprovalService`

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

#### Scenario: Pipeline stays in one approval unit for root matching

- **GIVEN** `/home/.netclaw/logs/` is in the approved `shell_execute` roots
- **WHEN** the agent runs `grep "error" /home/.netclaw/logs/crash.log | wc -l`
- **THEN** the pipeline is treated as one approval unit
- **AND** the unit is auto-approved because its recognized local filesystem path
  stays under the approved root
