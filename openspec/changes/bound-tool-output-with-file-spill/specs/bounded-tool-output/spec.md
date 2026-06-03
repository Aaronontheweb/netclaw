## ADDED Requirements

### Requirement: Single inline budget for tool output

Tool output returned inline to the model SHALL be bounded by a single budget
`N`, sourced from `SessionTuning.MaxInlineToolResultChars` (default 2000). When a
tool's output exceeds `N`, the inline result SHALL retain an `N`-character window
composed of the head and the tail of the output (each approximately `N/2`), with
the discarded middle replaced by a truncation marker. A tool SHALL NOT apply a
separate, larger inline budget that a later pipeline stage re-truncates.

#### Scenario: Output under budget returned whole

- **WHEN** a tool produces output of `N` characters or fewer
- **THEN** the full output is returned inline
- **AND** no spill file is created

#### Scenario: Output over budget returned as head and tail

- **WHEN** a tool produces output larger than `N` characters
- **THEN** the inline result contains the first ~`N/2` and the last ~`N/2`
  characters of the output
- **AND** a truncation marker separates the head from the tail

### Requirement: Full output spilled to a session-scoped file

When a tool's captured output exceeds the inline budget `N`, the system SHALL
write the captured output to `{sessionDirectory}/tool-calls/{toolCallId}.log` and
SHALL include that path in the inline result together with a message steering the
model to read ranges (`file_read` with offset/limit) or `grep` the file rather
than re-running the tool. Captured output SHALL be bounded by
`ToolConfig.MaxOutputChars` (the capture ceiling); output beyond the ceiling
SHALL be discarded with a "capture ceiling exceeded" marker while the source
continues to be drained so a live child process never deadlocks on a full pipe.

#### Scenario: Spill file written and path returned

- **WHEN** a tool produces output larger than `N` but within the capture ceiling
- **THEN** the captured output is written to
  `{sessionDirectory}/tool-calls/{toolCallId}.log`
- **AND** the inline result includes the file path and a steer to use `file_read`
  (offset/limit) or `grep`

#### Scenario: Capture ceiling bounds disk and keeps the pipe draining

- **WHEN** a tool produces output exceeding `MaxOutputChars`
- **THEN** only up to `MaxOutputChars` is captured to the spill file
- **AND** the source continues to be drained to completion
- **AND** the result notes that the capture ceiling was exceeded

### Requirement: Bounded-memory capture

Capturing tool output SHALL bound peak managed memory to the order of the capture
ceiling, independent of total output size. No capture path SHALL materialize the
entire output of an arbitrarily large source as a single in-memory string before
bounding it.

#### Scenario: Large output does not scale memory

- **WHEN** a source emits output far exceeding the capture ceiling
- **THEN** peak managed allocation stays on the order of the capture ceiling
- **AND** the capturing process is not OOM-killed by the capture itself

### Requirement: Redaction of emitted and foreign content

Secret redaction SHALL be applied on every model-facing path using the existing
`SecretOutputRedactor`. Output captured to a spill file the system emits SHALL be
redacted before the file is written (redact-on-write), in a single pass over the
bounded capture buffer. Content returned from reading a file the system did not
emit SHALL be redacted when it is returned to the model (redact-on-read).

#### Scenario: Spill file redacted on write

- **WHEN** captured output containing a secret is spilled to a file
- **THEN** the on-disk spill file has the secret redacted

#### Scenario: Foreign file redacted on read

- **WHEN** a file containing a secret is read and its contents returned to the model
- **THEN** the returned content has the secret redacted
