## ADDED Requirements

### Requirement: Execution outcome controls occurrence acknowledgement

Netclaw SHALL pass the Akka.Reminders envelope to every reminder execution. Netclaw SHALL acknowledge an occurrence only after successful execution and required delivery.

Netclaw SHALL send a negative acknowledgement after a known execution or delivery failure. The negative acknowledgement SHALL use the library retry budget.

#### Scenario: Channel execution fails before delivery

- **GIVEN** an enabled channel reminder occurrence is awaiting acknowledgement
- **WHEN** its session fails before required delivery succeeds
- **THEN** Netclaw sends a negative acknowledgement with the failure reason
- **AND** Netclaw does not send a successful acknowledgement
- **AND** Akka.Reminders persists the next attempt or a terminal state

#### Scenario: Execution and required delivery succeed

- **GIVEN** an enabled reminder occurrence is awaiting acknowledgement
- **WHEN** execution and required delivery succeed
- **THEN** Netclaw acknowledges the exact occurrence
- **AND** Akka.Reminders records `Delivered`

### Requirement: Reminder-level poison state is durable

Netclaw SHALL persist a consecutive execution failure count in the reminder definition. Each failed attempt SHALL increment the count, and a successful attempt SHALL reset it.

Netclaw SHALL disable the complete reminder when the count reaches `FailurePauseThreshold`. This count SHALL remain separate from the Akka.Reminders per-occurrence attempt count.

#### Scenario: Restart preserves the poison count

- **GIVEN** a reminder has three consecutive failed attempts
- **WHEN** the daemon restarts
- **THEN** reminder status reports three consecutive failures
- **AND** the next failed attempt increments the count to four

#### Scenario: Success resets the poison count

- **GIVEN** a reminder has one or more consecutive failed attempts
- **WHEN** a later attempt succeeds
- **THEN** Netclaw persists a zero consecutive failure count

#### Scenario: Fifth failure disables the complete reminder

- **GIVEN** a reminder has four consecutive failed attempts
- **WHEN** the next attempt fails
- **THEN** Netclaw disables the reminder
- **AND** Netclaw records a failed terminal outcome
- **AND** Netclaw cancels future occurrences for the complete reminder

### Requirement: One-shot reminders use soft deletion

Netclaw SHALL retain a one-shot definition after success or terminal failure. Netclaw SHALL disable the definition and record its terminal outcome.

Only an explicit delete command SHALL remove the definition and history.

#### Scenario: Successful one-shot remains inspectable

- **GIVEN** a one-shot reminder succeeds
- **WHEN** Netclaw completes its acknowledgement
- **THEN** Netclaw disables the definition with outcome `Completed`
- **AND** an all-reminders query returns the definition

#### Scenario: Failed one-shot remains enabled for retry

- **GIVEN** a one-shot attempt fails below the poison threshold
- **WHEN** Akka.Reminders schedules another attempt
- **THEN** Netclaw keeps the definition enabled
- **AND** reminder status shows the durable attempt state

#### Scenario: Reconciliation retains a past one-shot

- **GIVEN** a one-shot has a past fire time
- **WHEN** reconciliation finds no active schedule
- **THEN** reconciliation does not delete the definition or history
- **AND** reconciliation uses durable occurrence state to select restoration or a terminal soft delete

### Requirement: Reminder attempts have bounded acknowledgement leases

Netclaw SHALL use a one-hour absolute execution limit and a 70-minute Akka.Reminders acknowledgment timeout. It SHALL retain the 20-minute inactivity limit.

#### Scenario: Valid long execution completes within the lease

- **GIVEN** a reminder execution produces activity and completes within one hour
- **WHEN** required delivery succeeds
- **THEN** Netclaw acknowledges the occurrence before its 70-minute deadline

#### Scenario: Execution reaches the absolute limit

- **GIVEN** a reminder execution remains active for one hour
- **WHEN** the absolute limit expires
- **THEN** Netclaw stops the attempt
- **AND** Netclaw sends a negative acknowledgement
