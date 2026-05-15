## ADDED Requirements

### Requirement: Persisted reminder definitions carry required trust fields

A persisted `ReminderDefinition` SHALL declare its audience and boundary fields
as `required` and non-optional. Deserialization of a legacy reminder document
that lacks these fields SHALL fail loudly, identifying the document and the
missing field. The system SHALL NOT silently substitute a default audience or
boundary for a persisted reminder.

#### Scenario: Legacy reminder document missing trust fields fails loud

- **GIVEN** a persisted `ReminderDefinition` JSON document that predates this
  change and lacks an audience or boundary field
- **WHEN** the daemon attempts to deserialize it
- **THEN** deserialization throws an explicit error naming the document and
  the missing field
- **AND** no audience or boundary value is substituted

#### Scenario: Doctor detects and remediates legacy reminder documents

- **GIVEN** legacy reminder documents missing trust fields exist in the
  persistence directory
- **WHEN** the operator runs `netclaw doctor`
- **THEN** the check reports the affected documents
- **AND** `netclaw doctor --fix` backfills an explicit conservative
  (`Public`) audience and the public boundary after operator confirmation

#### Scenario: Reminder execution still rejects a missing audience

- **GIVEN** a reminder reaches execution
- **WHEN** the reminder definition's audience is somehow absent
- **THEN** the reminder execution actor throws rather than executing with a
  substituted audience
