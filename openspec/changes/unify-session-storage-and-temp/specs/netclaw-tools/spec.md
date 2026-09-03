This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Spawned child references are machine-actionable

A successful `spawn_agent` result SHALL return the child run identifier, an
exact child log path, and the exact child artifact directory. These paths SHALL
be below the current session envelope, so the parent can compose existing file
tools through the shared path access decision. A failed spawn SHALL NOT return
locations that appear usable.

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
- **THEN** the shared path access decision evaluates the file operation
- **AND** no new artifact-reference reader is required

#### Scenario: Example - parent reads child logs with existing tools

- **GIVEN** a successful spawn returned the exact child log path
- **WHEN** the owning parent uses `file_read`, `file_search`, or `file_list`
- **THEN** the existing tool performs its normal bounded operation
- **AND** no special child-log tool is required

#### Scenario: Counterexample - read permission does not grant writes

- **GIVEN** the parent audience permits reads but not writes
- **WHEN** it calls `file_write` or `file_edit` for that log
- **THEN** the `Write` path access decision denies the mutation
- **AND** the trusted-root relationship does not change that result

#### Scenario: Counterexample - failed spawn has no usable child references

- **WHEN** the child run is not created
- **THEN** the tool result reports failure
- **AND** it contains no child log path or artifact directory

#### Scenario: Example - successful child log path is ready

- **WHEN** `spawn_agent` returns a successful child result
- **THEN** the returned log path identifies an existing file
- **AND** an authorized `file_read` can open it immediately

### Requirement: One path access decision owns filesystem authorization

`netclaw-tools` SHALL own one path access decision for structured file tools,
project-directory declarations, unattended shell path facts, and shell calls
considered for reviewed-safe automatic authorization. The decision SHALL use:

- the canonical path;
- its relationship to a trusted root;
- the requested file operation;
- the audience policy; and
- protected-path and filesystem-link results.

The decision SHALL return an allowed or denied result. A denied result SHALL
carry one failure category and human-readable detail. A caller SHALL NOT repeat
root assembly, containment, or filesystem-link policy.

The existing `file_read`, `file_search`, `file_list`, `file_write`,
`file_edit`, and `attach_file` tools SHALL use this decision. They SHALL keep
their existing output, pagination, query, and approval contracts.

An interactive Personal shell call MAY reach explicit user approval for a path
outside trusted roots. That approval boundary is not reviewed-safe automatic
authorization and SHALL NOT create a second automatic path policy.

The Netclaw sessions root SHALL be a trusted root for parent and child runs.
A path in another session SHALL use the same decision as any other path below
that root. Session identity SHALL NOT add another access-control rule.

The system SHALL NOT add a log-specific tool, ownership check, projection, or
query language. `file_read` and `file_search` SHALL remain compatible with an
active log writer on POSIX and Windows.

#### Scenario: Example - one session reads another session's log

- **GIVEN** the audience permits `file_read`
- **AND** two sessions are below the Netclaw sessions root
- **WHEN** one session requests the other session's canonical log path
- **THEN** one `Read` path access decision allows the request
- **AND** `file_read` applies its normal output bounds

#### Scenario: Example - parent searches a child log

- **GIVEN** a parent receives a child log path from `spawn_agent`
- **WHEN** it calls `file_search` for that path
- **THEN** the shared path access decision evaluates the request
- **AND** the parent needs no shell or log-specific tool

#### Scenario: Example - an active Windows log remains readable

- **GIVEN** a log writer holds its append handle open
- **WHEN** `file_read` or `file_search` opens that log on Windows
- **THEN** the read succeeds
- **AND** the writer can append and flush another line

#### Scenario: Counterexample - a trusted root does not grant every operation

- **GIVEN** an audience permits `Read` but denies `Write`
- **WHEN** it requests both operations for one path below a trusted root
- **THEN** the shared decision allows the read
- **AND** it denies the write

#### Scenario: Counterexample - a filesystem link cannot escape

- **GIVEN** a path below a trusted root crosses a filesystem link outside it
- **WHEN** any file operation requests that path
- **THEN** the shared decision denies the request
- **AND** no caller can bypass that result with another path policy

### Requirement: Git worktrees compose existing tools

The existing `[session]` context SHALL announce the exact `worktree_dir`.
Agents SHALL create Git worktrees by calling `shell_execute` with a destination
below that directory. Normal shell authorization SHALL decide the command.
After Git succeeds, the agent SHALL use the existing
`set_working_directory` tool to adopt the created worktree as project scope.
The shared path access decision and normal shell authorization SHALL decide
the destination. The operation SHALL NOT use a separate worktree permission.

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

`netclaw.json` SHALL contain ordinary configuration. Its schema has no
secret-bearing fields. When a structured file read is otherwise authorized by trusted
roots and audience policy, `file_read` SHALL be able to read the exact
`netclaw.json` path. The implementation SHALL keep structured read-deny rules
independent from broader shell-deny indicators.

Secret-valued configuration SHALL be stored only in protected secret stores.
`secrets.json`, key material, OAuth token and credential material, webhook
secret material, the session database and sidecars, process-control files, and
similar protected state SHALL remain read-denied. A readable configuration
file SHALL NOT imply write, edit, attach, or shell authority.

This change SHALL NOT add content redaction, secret-field heuristics, or a
configuration migration path. Existing configuration validation and separate
secret storage remain authoritative.

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

#### Scenario: Counterexample - stored configuration is not effective configuration

- **GIVEN** an environment variable overrides a value from `netclaw.json`
- **WHEN** an agent reads `netclaw.json`
- **THEN** it receives the persisted non-secret file content
- **AND** the tool does not claim that the file explains the source or
  effective value of every configuration setting
