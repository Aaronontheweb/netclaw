This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Session storage binding is durable and versioned

Before a new-layout session creates a session-owned file, the system SHALL
persist an immutable storage binding with the layout version and absolute
session storage envelope root. The system SHALL use that binding when it
resumes the session. A configuration change, environment override, or binary
upgrade SHALL NOT reinterpret the envelope root. The binding SHALL NOT contain
a second log root. Two distinct raw session identifiers SHALL NOT resolve to
the same physical envelope, even when their human-readable sanitized forms are
equal.

Channel ingress, the parent actor, child-run creation, and the log dispatcher
SHALL resolve storage through one shared get-or-bind operation. That operation
SHALL be atomic for concurrent first consumers. A filesystem helper SHALL NOT
independently choose a new-layout path from only the session identifier and
current configuration.

#### Scenario: Example - new session binds one envelope before use

- **GIVEN** a new session has no storage binding
- **WHEN** it first needs session-owned filesystem storage
- **THEN** the system persists the version-2 binding before it creates a file
- **AND** every parent and child path derives from the persisted envelope

#### Scenario: Counterexample - configuration cannot relocate a bound session

- **GIVEN** a session has a persisted storage binding for
  `/srv/netclaw-a/sessions/s-42`
- **WHEN** configuration changes the sessions base to
  `/srv/netclaw-b/sessions`
- **THEN** the session continues to use the persisted envelope under
  `/srv/netclaw-a`
- **AND** the system does not move, copy, or partly reinterpret the session

#### Scenario: Counterexample - binding failure prevents an untracked write

- **GIVEN** the system cannot persist the storage binding
- **WHEN** a filesystem operation needs the new layout
- **THEN** the operation fails before it writes session-owned data
- **AND** the system does not derive a fallback root from current configuration

#### Scenario: Example - ingress binds before it writes media

- **GIVEN** the first message for a new session contains an attachment
- **WHEN** channel ingress prepares the media file before actor processing
- **THEN** it resolves and persists the storage binding first
- **AND** it writes the file below `<session-envelope>/workspace/media`

#### Scenario: Example - concurrent first messages share one binding

- **GIVEN** two ingress requests race to create one new session
- **WHEN** both call the shared storage resolver
- **THEN** one atomic binding wins
- **AND** both requests receive the same persisted envelope root

#### Scenario: Counterexample - sanitized identifiers cannot collide

- **GIVEN** raw session identifiers `channel/a_b` and `channel/a/b`
- **AND** their display-safe forms would otherwise be equal
- **WHEN** the resolver binds storage for both sessions
- **THEN** it persists two different envelope roots
- **AND** later recovery maps each raw identifier to its original root

#### Scenario: Counterexample - helper cannot bypass layout selection

- **GIVEN** a new session has no binding yet
- **WHEN** an ingress or logging helper needs a path
- **THEN** the helper does not compute a writable path from only the session ID
  and configured base
- **AND** no file is created before the shared resolver selects the layout

### Requirement: Existing sessions resume without migration

The system SHALL leave the storage binding absent for a session that predates
the new layout. It SHALL continue to use the existing session-directory and
session-log path resolvers for that session. An upgrade SHALL NOT move, copy,
rename, or delete its data.

#### Scenario: Example - legacy session resumes after upgrade

- **GIVEN** a persisted session predates the storage binding
- **WHEN** a current binary resumes it
- **THEN** the storage binding remains absent
- **AND** the system uses the existing session and log path resolvers
- **AND** no migration changes the existing files

#### Scenario: Counterexample - legacy session cannot become a hybrid

- **GIVEN** an existing unbound session has separate data and log directories
- **WHEN** a current binary resumes it without storage reconfiguration
- **THEN** both existing path resolvers remain in use
- **AND** the system does not route new logs into a new-layout envelope

#### Scenario: Counterexample - old binary support for new sessions is out of scope

- **GIVEN** an older binary does not understand the storage binding
- **WHEN** release documentation describes compatibility
- **THEN** it promises that current binaries preserve existing unbound sessions
- **AND** it does not promise that a pre-feature binary can resume a newly
  bound session

#### Scenario: Example - journal-only legacy session remains discoverable

- **GIVEN** an existing session has journal records but no snapshot and no
  storage binding
- **WHEN** the current resolver checks whether the session predates the new
  layout
- **THEN** it recognizes the shipped journal schema and table
- **AND** it resumes the existing path behavior without creating a new binding

### Requirement: Version 2 uses one physical session envelope

For a session with a version-2 binding, the system SHALL place the parent
session directory, artifacts, temporary files, worktrees, raw log, and all
child-run directories below the persisted session storage envelope. Each child
run SHALL place its artifacts, temporary files, and raw log below
`<session-envelope>/subagents/<run-id>`. Daemon-global logs SHALL remain outside
the session envelope.

The parent session directory SHALL be `<session-envelope>/workspace`. Raw logs
SHALL use `<session-envelope>/logs/session.log` for the parent and
`<session-envelope>/subagents/<run-id>/logs/session.log` for a child.

#### Scenario: Example - one envelope contains parent and child data

- **GIVEN** a version-2 parent at `/srv/netclaw/sessions/s-42`
- **AND** the parent creates child run `run-7`
- **WHEN** the parent and child resolve their storage paths
- **THEN** the parent cwd is `/srv/netclaw/sessions/s-42/workspace`
- **AND** the parent raw log is
  `/srv/netclaw/sessions/s-42/logs/session.log`
- **AND** the child artifacts, temporary files, and raw log are below
  `/srv/netclaw/sessions/s-42/subagents/run-7`

#### Scenario: Example - sibling child runs do not share storage

- **GIVEN** child runs `run-7` and `run-8` belong to one parent
- **WHEN** the system derives their paths
- **THEN** each child path contains its own opaque run identifier
- **AND** neither child's artifact, temporary, or log path is below the other
  child's directory

#### Scenario: Counterexample - new raw logs cannot use a second root

- **GIVEN** a version-2 session
- **WHEN** the log dispatcher resolves a parent or child target
- **THEN** the target is below the persisted session envelope
- **AND** it is not below `NetclawPaths.SessionLogsDirectory`

#### Scenario: Counterexample - daemon logs do not enter session storage

- **GIVEN** the daemon emits a process-wide diagnostic
- **WHEN** the diagnostic is written
- **THEN** it uses the daemon-global log location
- **AND** it is not written to a session envelope

### Requirement: Current session is an implicit trusted root

The system SHALL treat the complete current session storage envelope as an
implicit trusted root for every parent and child run in that session. It SHALL
also inherit the configured trusted roots into those runs. Existing audience
profiles and per-operation tool permissions SHALL decide whether a run can
read, list, search, write, edit, attach, or execute against a path below an
effective root. Shell syntax analysis, approval policy, and tool exposure SHALL
still apply.

The effective filesystem authority SHALL be:

```text
current session envelope
+ inherited configured trusted roots
+ current audience and operation permissions
```

The system SHALL NOT add a log-specific read scope, child-artifact ownership
ACL, foreign-session override, or managed-data exception. A path in another
Netclaw session SHALL use the same ordinary root and audience rules as any
other path. Personal `Mode.All` can therefore inspect another session when its
normal roots cover that path. Team and Public runs can do so only when their
configured roots and file-tool permissions cover it.

For an existing unbound session, the system SHALL derive the current-session
implicit roots from the unchanged legacy session and log locations. It SHALL
NOT move or copy legacy files to make them accessible.

The default no-project working directory SHALL remain `workspace/`. The
implementation SHALL NOT redefine `{session_dir}` as the session envelope or
use the complete envelope as the default shell cwd. Root authority SHALL NOT be
treated as unconditional shell approval. Existing link, reparse-point,
protected-path, and control-plane checks SHALL still apply.

This requirement defines Netclaw application authorization. It SHALL NOT be
documented or tested as OS-level containment of an arbitrary process that has
already received execution authority under the Netclaw identity.

#### Scenario: Example - default recursive search stays in workspace

- **GIVEN** a version-2 session has no project scope
- **WHEN** a shell starts without an explicit working directory
- **THEN** its cwd is `<session-envelope>/workspace`
- **AND** a recursive search of `.` does not include the sibling `logs/` or
  `subagents/` areas by directory containment

#### Scenario: Example - agent reads its own session log

- **GIVEN** an agent uses a version-2 session envelope
- **WHEN** it calls `file_read` for its main session log
- **THEN** current-session root authority permits the path
- **AND** `file_read` applies its normal output bounds

#### Scenario: Example - parent reads a child log

- **GIVEN** a parent owns child run `run-7`
- **WHEN** it calls `file_search` on the returned log path's directory
- **THEN** current-session root authority permits the path
- **AND** no special log tool is required

#### Scenario: Example - child reads another log in the same session

- **GIVEN** child runs `run-7` and `run-8` belong to one session
- **WHEN** `run-7` reads the main log or the log for `run-8`
- **THEN** current-session root authority permits the path
- **AND** the request remains subject to normal file-tool limits

#### Scenario: Example - legacy session keeps readable log paths

- **GIVEN** an existing unbound session uses separate data and log roots
- **WHEN** its parent or child calls an existing file tool for a resolved
  same-session log path
- **THEN** its derived current-session roots permit the operation
- **AND** no file moves into a new envelope

#### Scenario: Example - trusted root can cover another session

- **GIVEN** a Personal run has ordinary read authority for a configured root
  that contains another Netclaw session
- **WHEN** it calls `file_read` for that session's log
- **THEN** normal trusted-root and read policy decides the call
- **AND** the path is not denied only because it belongs to another session

#### Scenario: Counterexample - path knowledge does not grant mutation

- **GIVEN** an audience profile permits `file_read` but not `file_write`
- **WHEN** it calls `file_write` or `file_edit` for a log path
- **THEN** current-session containment does not authorize that operation
- **AND** normal write policy decides the call

#### Scenario: Example - child artifact uses ordinary file authority

- **GIVEN** a parent receives a child artifact path below its current session
- **WHEN** it uses an existing file tool allowed by its audience profile
- **THEN** current-session root authority permits the path
- **AND** no child ownership record or artifact-specific reader is required

#### Scenario: Counterexample - linked path cannot escape current-session root

- **GIVEN** a same-session log directory contains a filesystem link to another
  session
- **WHEN** an agent reads, lists, or searches through that link
- **THEN** existing path safety policy denies the operation
- **AND** current-session authority does not bypass that denial

#### Scenario: Counterexample - trusted root itself cannot be a link escape

- **GIVEN** `logs`, `tmp`, `artifacts`, `worktrees`, `workspace`, or a legacy
  current-session root is a symbolic link, junction, or reparse point outside
  its expected parent
- **WHEN** a file tool resolves a path through that root
- **THEN** containment validation denies the operation
- **AND** validation does not skip the trusted root segment itself

#### Scenario: Counterexample - envelope is not the default shell cwd

- **GIVEN** an agent can read logs in its session envelope
- **WHEN** policy selects a default shell cwd
- **THEN** it uses the session directory or existing project scope
- **AND** it does not use the complete envelope merely because that envelope is
  an effective trusted root

#### Scenario: Example - shell can target the session worktree area

- **GIVEN** an audience can use `shell_execute`
- **AND** its current session root contains `worktree_dir`
- **WHEN** a shell call targets a path below `worktree_dir`
- **THEN** normal shell root checks recognize the path as inside the current
  session
- **AND** normal syntax, hard-deny, and approval rules still decide execution

#### Scenario: Counterexample - this layout is not a process sandbox

- **GIVEN** an arbitrary process has already received execution authority as
  the Netclaw OS identity
- **WHEN** it learns a same-session log path
- **THEN** this storage layout alone does not claim to stop the OS file open
- **AND** a future containment capability must define that stronger boundary
