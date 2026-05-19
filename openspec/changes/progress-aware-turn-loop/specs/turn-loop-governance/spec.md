## ADDED Requirements

### Requirement: Tool iteration progress classification

The turn loop SHALL classify each completed tool-execution iteration as either
productive or unproductive. An iteration SHALL be classified productive when at
least one tool result in the batch is not an error AND is not a duplicate of a
tool-call fingerprint (tool name plus arguments) already executed earlier in the
same turn. Otherwise the iteration SHALL be classified unproductive.

#### Scenario: Iteration with new successful results is productive

- **GIVEN** a turn has executed one or more tool iterations
- **WHEN** a tool batch completes with at least one non-error result for a
  tool-call fingerprint not seen earlier this turn
- **THEN** the iteration is classified productive
- **AND** the consecutive-unproductive counter is reset to zero

#### Scenario: Iteration of only errors is unproductive

- **WHEN** a tool batch completes and every result in the batch is an error
- **THEN** the iteration is classified unproductive

#### Scenario: Iteration of only repeated calls is unproductive

- **WHEN** a tool batch completes and every result is a duplicate of a tool-call
  fingerprint already executed earlier in the same turn
- **THEN** the iteration is classified unproductive

### Requirement: Wrap-up on sustained lack of progress

The turn loop SHALL wrap a turn up when it stops making progress. When the number
of consecutive unproductive iterations reaches the configured
`UnproductiveIterationLimit`, the loop SHALL stop issuing further tool batches and
enter the wrap-up handoff. A single productive iteration SHALL reset the
consecutive-unproductive count to zero so a recovering turn continues. The wrap-up
SHALL be recorded with a structured log event identifying the lack-of-progress
reason.

#### Scenario: Consecutive unproductive iterations trigger wrap-up

- **GIVEN** `UnproductiveIterationLimit` is 3
- **WHEN** 3 consecutive iterations are classified unproductive
- **THEN** the turn loop enters the wrap-up handoff
- **AND** a structured log event records the wrap-up with a lack-of-progress reason

#### Scenario: A productive iteration resets the counter

- **GIVEN** `UnproductiveIterationLimit` is 3
- **AND** 2 consecutive iterations have been classified unproductive
- **WHEN** the next iteration is classified productive
- **THEN** the consecutive-unproductive count is reset to zero
- **AND** the turn loop continues normally

### Requirement: Iteration-count safety fuse

The turn loop SHALL enforce `MaxToolIterationsPerTurn` as the hard upper bound on
the number of LLM-to-tool iterations within a turn. Exactly one iteration SHALL
be counted per LLM response that requests tools, regardless of how many tool
calls that response contains. Reaching the fuse SHALL trigger the wrap-up
handoff. The fuse SHALL NOT be expressed as a wall-clock duration or as a count
of individual tool calls.

#### Scenario: Reaching the iteration fuse triggers wrap-up

- **GIVEN** `MaxToolIterationsPerTurn` is 60
- **WHEN** a turn completes its 60th tool iteration without finishing
- **THEN** the turn loop enters the wrap-up handoff
- **AND** a structured log event records the wrap-up with a fuse reason

#### Scenario: Parallel tool calls count as a single iteration

- **WHEN** one LLM response requests 8 tool calls in parallel
- **THEN** the turn's iteration count increases by exactly 1

### Requirement: Model-facing scale advisory

The turn loop SHALL surface a scale advisory to the model once a turn has
progressed beyond an initial iteration threshold. The advisory SHALL report the
current iteration count and approximate cumulative token usage, and SHALL
instruct the model to checkpoint findings and to prefer delegating independent
sub-tasks to subagents. The advisory SHALL be injected as ephemeral context that
is recomputed and refreshed on each LLM call and SHALL NOT be persisted to the
turn's conversation history. The advisory SHALL NOT, by itself, terminate a turn.

#### Scenario: Advisory injected and refreshed each call

- **GIVEN** a turn has progressed beyond the advisory's initial iteration threshold
- **WHEN** the next LLM call is issued
- **THEN** a scale advisory reflecting the current iteration count and cumulative
  token usage is included in that call's context

#### Scenario: Advisory is not persisted to conversation history

- **WHEN** a scale advisory is surfaced for an LLM call
- **THEN** the advisory is not written to the persisted turn history
- **AND** it does not appear in compaction input or the persisted `TurnRecorded` event

### Requirement: Graceful turn handoff preserves completed work

The session SHALL preserve completed work when a turn reaches a wrap-up condition.
When a turn reaches any wrap-up condition (sustained lack of progress or the
iteration fuse), the session SHALL deliver a final reply containing the work
completed during the turn. The session SHALL NOT fail the turn for resource
reasons. When the task is not complete, the final reply SHALL state what was
completed and what remains so the work can be resumed on a later turn.

#### Scenario: Wrap-up delivers completed work

- **GIVEN** a turn has completed substantive tool work
- **WHEN** a wrap-up condition is reached
- **THEN** the session delivers a final reply summarizing the completed work
- **AND** the turn is not failed

#### Scenario: Incomplete task states remaining work

- **GIVEN** a turn reaches a wrap-up condition before the task is complete
- **WHEN** the final reply is produced
- **THEN** the reply states what was completed and what remains
- **AND** the reply is persisted as the turn's assistant reply

#### Scenario: Resource exhaustion never fails the turn

- **WHEN** a turn wraps up due to lack of progress or the iteration fuse
- **THEN** the session does not invoke the turn-failure path
- **AND** the user receives a delivered reply rather than an error message

### Requirement: Closed-tool recovery during wrap-up

During wrap-up the session SHALL request a final reply with tool execution
disabled. If that response still contains tool calls, the session SHALL
synthesize a tool result for every tool-use block stating that tool execution is
closed for the turn, and SHALL re-prompt the model once more with tools disabled.
The closed-tool re-prompt SHALL be bounded to a single attempt. If no usable
reply text is produced after that attempt, the session SHALL deliver the best
available partial result. The session SHALL NOT fail the turn because the model
requested a tool after tool execution was disabled.

#### Scenario: Tool call after disable is recovered, not failed

- **GIVEN** the session has requested a wrap-up reply with tools disabled
- **WHEN** the model's response still contains tool calls
- **THEN** the session synthesizes a closed-tool result for every tool-use block
- **AND** re-prompts the model once with tools disabled
- **AND** does not fail the turn

#### Scenario: Bounded re-prompt then partial delivery

- **GIVEN** the closed-tool re-prompt has already been attempted once
- **WHEN** the model still produces no usable reply text
- **THEN** the session delivers the best available partial result
- **AND** the turn is not failed
