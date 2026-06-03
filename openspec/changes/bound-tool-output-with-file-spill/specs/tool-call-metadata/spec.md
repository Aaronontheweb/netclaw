## ADDED Requirements

### Requirement: Tool call identifier available to tools

The tool-call identifier SHALL be exposed to executing tools via
`ToolExecutionContext.ToolCallId`, set by the session pipeline at context-build
time from the same identifier carried in the call's `ToolCallMeta`. Tools that
emit per-call artifacts (e.g. a spilled output file under
`{sessionDirectory}/tool-calls/{toolCallId}.log`) SHALL use this identifier to
name them. The identifier SHALL be unique per tool call within a session.

#### Scenario: Tool reads its call id from the context

- **GIVEN** the pipeline builds a `ToolExecutionContext` for a tool call
- **WHEN** the tool executes
- **THEN** `ToolExecutionContext.ToolCallId` equals the call's `ToolCallMeta`
  identifier

#### Scenario: Spill file is named by call id

- **GIVEN** a tool spills output to a file
- **WHEN** the file is written
- **THEN** its name is derived from `ToolExecutionContext.ToolCallId`
- **AND** two concurrent tool calls in the same session do not collide on the path
