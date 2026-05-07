## MODIFIED Requirements

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
safety backstop for directory-root approvals. `ToolPathPolicy` SHALL resolve
symlinks along every component of a candidate path so that a planted symlink
under an approved root cannot be used to reach a protected path that lies
outside that root.

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

#### Scenario: Symlink under approved root cannot reach a protected path

- **GIVEN** `/home/user/safe/` is approved for `shell_execute`
- **AND** `/home/user/safe/leak` is a directory symlink whose target resolves
  to `/etc`
- **WHEN** the agent runs `cat /home/user/safe/leak/passwd`
- **THEN** the approval gate auto-approves the unit because the literal path
  is within the approved root
- **AND** `ToolPathPolicy.CommandReferencesDeniedPath` blocks execution because
  the canonical path resolves to `/etc/passwd` after symlink resolution along
  every path component
