## MODIFIED Requirements

### Requirement: Tool execution encapsulation

Tool execution SHALL be encapsulated in a `SessionToolExecutionPipeline` static
utility class. The pipeline SHALL execute tool calls in parallel, track sub-agent
activity, and send `ToolExecutionCompleted` or `ToolExecutionFailed` back to the
actor. The pipeline SHALL bound inline tool results to
`SessionTuning.MaxInlineToolResultChars` (default 2000) using **head+tail**
retention (not head-only), so the tail of a result is preserved. For tools that
already bound their own output to that budget via the `bounded-tool-output`
capability, this clamp is a no-op safety net; it remains the bounding point for
results that do not flow through the shared bounded-output mechanism (e.g. MCP
tool results and in-process tools).

#### Scenario: Parallel tool execution

- **GIVEN** an LLM response contains 3 tool calls
- **WHEN** `SessionToolExecutionPipeline.ExecuteToolsAsync()` runs
- **THEN** all 3 tool calls execute in parallel
- **AND** results are collected and sent as a single `ToolExecutionCompleted`

#### Scenario: Tool execution timeout

- **GIVEN** tool execution is in progress
- **WHEN** the configured `ToolExecutionTimeout` elapses
- **THEN** the pipeline sends `ToolExecutionFailed` with a `TimeoutException`

#### Scenario: Oversized non-shared result clamped head and tail

- **GIVEN** a tool result that did not pass through the shared bounded-output
  mechanism exceeds `MaxInlineToolResultChars`
- **WHEN** the pipeline clamps it
- **THEN** the clamped result retains both the head and the tail within the budget
- **AND** the tail of the result is not discarded
