This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Spawned child references are machine-actionable

A successful `spawn_agent` result SHALL return the child run identifier, an
exact child log path, and the exact child artifact directory. These paths SHALL
be below the current session envelope, so the parent can compose existing file
tools under normal current-session root and audience policy. A failed spawn
SHALL NOT return locations that appear usable.

The system SHALL resolve and create the child log target before it returns a
successful result. The log can be empty. An immediate authorized `file_read`
SHALL NOT fail because the log path is not ready.

The result shape SHALL be equivalent to:

```text
run_id: "run-7"
log_path: "/srv/netclaw/sessions/s-42/subagents/run-7/logs/session.log"
artifact_dir: "/srv/netclaw/sessions/s-42/subagents/run-7/artifacts"
```

#### Scenario: Example - successful spawn returns child references

- **WHEN** a parent successfully starts a child run
- **THEN** the tool result contains the child run identifier
- **AND** it contains the exact child log path and artifact directory
- **AND** both paths belong to that parent session

#### Scenario: Example - parent reads a child artifact with an existing tool

- **GIVEN** a successful spawn returned the child artifact directory
- **WHEN** the owning parent calls `file_read` or `attach_file` for a file below
  that directory
- **THEN** current-session root authority can satisfy the path check
- **AND** no new artifact-reference reader is required

#### Scenario: Example - parent reads child logs with existing tools

- **GIVEN** a successful spawn returned the exact child log path
- **WHEN** the owning parent uses `file_read`, `file_search`, or `file_list`
- **THEN** the existing tool performs its normal bounded operation
- **AND** no special child-log tool is required

#### Scenario: Counterexample - read permission does not grant writes

- **GIVEN** the parent audience permits reads but not writes
- **WHEN** it calls `file_write` or `file_edit` for that log
- **THEN** current-session containment does not authorize the mutation
- **AND** normal write policy decides the call

#### Scenario: Counterexample - failed spawn has no usable child references

- **WHEN** the child run is not created
- **THEN** the tool result reports failure
- **AND** it contains no child log path or artifact directory

#### Scenario: Example - successful child log path is ready

- **WHEN** `spawn_agent` returns a successful child result
- **THEN** the returned log path identifies an existing file
- **AND** an authorized `file_read` can open it immediately

### Requirement: Existing file tools can inspect session data

The existing `file_read`, `file_search`, `file_list`, `file_write`,
`file_edit`, and `attach_file` tools SHALL accept session-data paths when the
effective roots, audience profile, and operation permissions authorize the
call. Each tool SHALL keep its existing bounds, pagination, query, and
approval contract. This capability SHALL NOT add a new tool, ownership ACL, or
log-specific query language.

The current session envelope SHALL be an implicit trusted root. Parent and
child runs SHALL inherit configured trusted roots. A path in another session
SHALL use those normal roots and permissions; it SHALL NOT receive a special
allow or deny because it is session data.

The existing file tools SHALL return their normal file content. The system
SHALL NOT add a log-specific redaction or projection layer. Existing file-tool
output bounds and audience policy SHALL still apply.

`file_read` and `file_search` SHALL support an active session-log writer on
POSIX and Windows. Their read handles SHALL NOT block the writer or fail only
because the writer keeps its append handle open.

#### Scenario: Example - parent reads the next child log page

- **GIVEN** a parent receives the child log path from `spawn_agent`
- **WHEN** it calls `file_read` with `StartLine=1` and a bounded `Limit`
- **THEN** the tool returns that normal line range
- **AND** the parent can request the next range with a later `StartLine`

#### Scenario: Example - parent searches child logs with an existing tool

- **GIVEN** a parent receives a child log path
- **WHEN** it calls `file_search` on that path's directory in content mode
- **THEN** the tool returns its normal bounded matches
- **AND** the parent does not need a shell search

#### Scenario: Example - agent lists its session logs

- **GIVEN** an agent can use `file_list` in its current session root
- **WHEN** it calls `file_list` for its session log area
- **THEN** the tool lists paths below the requested authorized directory
- **AND** it applies its normal result limit

#### Scenario: Example - active Windows log remains readable

- **GIVEN** the session-log writer holds its normal append handle open
- **WHEN** `file_read` or `file_search` opens that log on Windows
- **THEN** the read succeeds with the normal file-tool result
- **AND** the writer can append and flush another line

#### Scenario: Counterexample - same-session log gets no special projection

- **GIVEN** a same-session log contains normal session diagnostic content
- **WHEN** an authorized agent reads it with `file_read`
- **THEN** the tool returns its normal bounded file content
- **AND** Netclaw does not replace it with a log-specific activity view

#### Scenario: Example - configured root covers another session

- **GIVEN** an agent's configured trusted root contains another session log
- **WHEN** its audience and file-read permissions allow the operation
- **THEN** the existing file tool can read that path
- **AND** no foreign-session override is required

#### Scenario: Example - log path survives parent recovery

- **GIVEN** a parent received a child log path before an actor restart
- **WHEN** the recovered parent reads that path
- **THEN** the recovered current-session root authorizes the same child path
- **AND** the existing file tool applies its current output limits

### Requirement: Git worktrees compose existing tools

The existing `[session]` context SHALL announce the exact `worktree_dir`.
Agents SHALL create Git worktrees by calling `shell_execute` with a destination
below that directory. Normal shell authorization SHALL decide the command.
After Git succeeds, the agent SHALL use the existing
`set_working_directory` tool to adopt the created worktree as project scope.
The destination's current-session root authority SHALL dovetail with shell
access; it SHALL NOT use a separate worktree permission.

The system SHALL NOT add `worktree_create`, a worktree-specific authorization
model, or a worktree ownership record. It SHALL NOT parse private Git option
grammar to infer authority. Automatic cleanup remains out of scope.

#### Scenario: Example - current project gets a managed worktree

- **GIVEN** the current project is an authorized Git repository
- **AND** session context provides
  `worktree_dir=/srv/netclaw/sessions/s-42/worktrees`
- **WHEN** the agent runs `git worktree add` through `shell_execute` with a
  destination below `worktree_dir`
- **AND** Git succeeds
- **THEN** the agent can pass that destination to `set_working_directory`
- **AND** existing project-scope behavior loads project instructions

#### Scenario: Counterexample - external destination gets no special authority

- **WHEN** an agent authors `git worktree add /tmp/fix-branch branch-name`
- **THEN** normal shell policy evaluates the authored command
- **AND** `worktree_dir` guidance does not rewrite or auto-approve it

#### Scenario: Counterexample - unauthorized source repository is denied

- **GIVEN** a requested source repository is outside current authority
- **WHEN** the agent submits the Git command through `shell_execute`
- **THEN** authorization denies the operation
- **AND** no worktree-specific tool bypasses that decision

#### Scenario: Counterexample - failed worktree does not change project scope

- **WHEN** worktree creation fails or is denied
- **THEN** the project scope remains unchanged
- **AND** the agent does not call `set_working_directory` for a failed result

#### Scenario: Counterexample - no custom worktree tool is exposed

- **WHEN** the dynamic tool catalog is assembled
- **THEN** it contains the existing shell and working-directory tools
- **AND** it does not contain `worktree_create`

### Requirement: Ordinary configuration is readable without exposing secrets

`netclaw.json` SHALL contain ordinary configuration and SHALL NOT contain
secret values. When a structured file read is otherwise authorized by trusted
roots and audience policy, `file_read` SHALL be able to read the exact
`netclaw.json` path. The implementation SHALL keep structured read-deny rules
independent from broader shell-deny indicators.

Secret-valued configuration SHALL be stored only in protected secret stores.
`secrets.json`, key material, OAuth token and credential material, webhook
secret material, the session database and sidecars, process-control files, and
similar protected state SHALL remain read-denied. A readable configuration
file SHALL NOT imply write, edit, attach, or shell authority.

The implementation SHALL detect a legacy or manually edited `netclaw.json`
that contains a known secret-valued field before it makes the file readable to
an agent. It SHALL migrate the value to the protected secret store or fail
closed with an operator-facing validation error. It SHALL NOT silently expose
the raw file. Secret classification SHALL come from typed configuration or
provider metadata, not from guessing by field name or value format.

#### Scenario: Example - agent reads ordinary stored configuration

- **GIVEN** `netclaw.json` contains no secret-valued fields
- **AND** the current audience can use `file_read` under a trusted root that
  contains the configuration file
- **WHEN** the agent calls `file_read` for the exact `netclaw.json` path
- **THEN** the tool returns its normal bounded file content
- **AND** no shell command or special configuration reader is required

#### Scenario: Counterexample - secret store remains denied

- **GIVEN** the same agent can read ordinary configuration
- **WHEN** it requests `secrets.json`, key material, OAuth credentials, or
  webhook secret material
- **THEN** protected-path policy denies the read
- **AND** the result does not include secret content

#### Scenario: Counterexample - read authority does not grant mutation

- **GIVEN** `file_read` can read `netclaw.json`
- **WHEN** the agent calls `file_write`, `file_edit`, `attach_file`, or a shell
  command for that path
- **THEN** the read decision is not reused
- **AND** the operation follows its independent policy

#### Scenario: Counterexample - inline legacy secret fails closed

- **GIVEN** a manually edited `netclaw.json` contains a known secret-valued
  provider credential, MCP environment value or header, notification webhook
  URL or header, or skill-feed API key
- **WHEN** the daemon validates agent-readable configuration
- **THEN** it migrates the value to protected storage or reports a blocking
  operator error
- **AND** `file_read` does not return the unprotected secret value

#### Scenario: Counterexample - stored configuration is not effective configuration

- **GIVEN** an environment variable overrides a value from `netclaw.json`
- **WHEN** an agent reads `netclaw.json`
- **THEN** it receives the persisted non-secret file content
- **AND** the tool does not claim that the file explains the source or
  effective value of every configuration setting
