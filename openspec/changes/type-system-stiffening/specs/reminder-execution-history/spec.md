## ADDED Requirements

### Requirement: Persisted reminder definitions carry required trust fields

A persisted `ReminderDefinition` SHALL declare its audience and boundary fields
as `required` and non-optional, so that every in-process construction is
enforced by the compiler. A legacy reminder JSON document that lacks these
fields SHALL be backfilled at load with a conservative fail-closed value —
`Public` audience and the public boundary — and the backfill SHALL be logged at
warning level naming the document. The system SHALL NOT backfill an elevated
default.

#### Scenario: Legacy reminder document is backfilled fail-closed at load

- **GIVEN** a persisted `ReminderDefinition` JSON document that predates this
  change and lacks an audience or boundary field
- **WHEN** the reminder store deserializes it
- **THEN** the missing audience is set to `Public` and the missing boundary to
  the public boundary
- **AND** a warning naming the document is logged
- **AND** the reminder remains executable with no loss of its other fields

#### Scenario: Current reminder documents round-trip unchanged

- **GIVEN** a `ReminderDefinition` written after this change with explicit
  audience and boundary
- **WHEN** the reminder store deserializes it
- **THEN** the audience and boundary are read verbatim with no warning logged
