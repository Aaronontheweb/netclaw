## MODIFIED Requirements

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool that runs commands as the
Netclaw process user context. Stdin SHALL be closed (no interactive commands).
Execution SHALL enforce a configurable timeout (default: 60 seconds). The
combined stdout+stderr output SHALL be bounded as model-facing tool output per
the `bounded-tool-output` capability — a single inline budget `N`
(`MaxInlineToolResultChars`) with head+tail retention, and a spill to
`{sessionDirectory}/tool-calls/{toolCallId}.log` with a steering message when the
output exceeds `N`. The combined output SHALL share one budget across stdout and
stderr (not a separate per-stream budget). Before execution, the shell tool SHALL
check the hard deny list via `ShellCommandPolicy`. Hard-denied commands SHALL be
rejected before `ToolPathPolicy` path checks.

#### Scenario: Execute command and return output

- **GIVEN** the `shell` grant is available for the session
- **WHEN** the agent invokes the shell tool with a command
- **THEN** the command is executed as the Netclaw process user
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

#### Scenario: Hard-denied command rejected before execution

- **GIVEN** the agent invokes `shell_execute` with `netclaw daemon stop`
- **WHEN** `ShellCommandPolicy` evaluates the command
- **THEN** the command is rejected with "Command blocked by hard deny policy"
- **AND** the shell process is never started

#### Scenario: Execution timeout enforced

- **GIVEN** a shell command is running
- **WHEN** the command exceeds the configured timeout (default: 60 seconds)
- **THEN** the process is terminated
- **AND** the tool returns a timeout error message to the LLM

#### Scenario: Output over budget spills to a file with a steer

- **GIVEN** a shell command produces combined output exceeding the inline budget `N`
- **WHEN** the output is captured
- **THEN** the inline result contains the head and tail of the output within `N`
- **AND** the full (redacted) output is written to
  `{sessionDirectory}/tool-calls/{toolCallId}.log`
- **AND** the inline result includes that path and a steer to read ranges with
  `file_read` (offset/limit) or `grep` instead of re-running the command

#### Scenario: Stdin closed prevents interactive commands

- **GIVEN** the agent invokes the shell tool with a command
- **WHEN** the process is created
- **THEN** stdin is closed immediately
- **AND** commands that require interactive input fail promptly

#### Scenario: Working directory set to project path

- **GIVEN** the session is associated with a registered project
- **WHEN** the shell tool executes a command
- **THEN** the working directory is set to the project's registered path

## ADDED Requirements

### Requirement: File read tool bounds output and redacts

The `file_read` tool SHALL bound the content it returns to the inline budget `N`
per the `bounded-tool-output` capability and SHALL run the returned content
through `SecretOutputRedactor` before returning it (redact-on-read). For a file
larger than `N`, `file_read` SHALL return a head+tail sample within `N` and a
message steering the model to read a specific range (`offset`/`limit`) or `grep`,
rather than materializing the entire file in memory. `file_read` SHALL NOT copy
the file to a separate spill path — the file on disk is its own backing store.
The existing line-range (`offset`/`limit`) path SHALL remain bounded.

#### Scenario: Large file returns a bounded sample and a steer

- **WHEN** the agent reads a file larger than `N` with no `offset`/`limit`
- **THEN** the tool returns a head+tail sample within `N`
- **AND** the tool does not materialize the whole file in memory
- **AND** the result steers the model to use `offset`/`limit` or `grep`

#### Scenario: Secrets in a read file are redacted

- **GIVEN** a file contains a secret-bearing value (e.g. an API key)
- **WHEN** the agent reads the file
- **THEN** the returned content has the secret redacted
