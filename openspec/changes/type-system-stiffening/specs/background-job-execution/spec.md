## MODIFIED Requirements

### Requirement: Job delivery carries originating audience

Background job results delivered via `DeliverTrustedSessionTurn` SHALL carry
the originating session's `TrustAudience` and trust boundary. The job
definition SHALL persist these values at creation time as `required`,
non-optional fields. Trusted delivery SHALL be scoped to that originating
session and persisted originating audience/boundary only.

Background-job submission SHALL fail loudly when no turn source is present. The
submission path SHALL NOT default a missing audience to `TrustAudience.Personal`
or a missing boundary to the personal boundary; a missing turn source is a
programming error and SHALL raise an explicit exception.

#### Scenario: Job delivery uses originating audience

- **GIVEN** a background job was started from a Personal-audience session
- **WHEN** the job completes and delivers results
- **THEN** `DeliverTrustedSessionTurn` carries `TrustAudience.Personal`
- **AND** the session processes the turn with Personal-level grants

#### Scenario: Trusted delivery remains scoped to originating boundary

- **GIVEN** a background job was started with a specific originating trust
  boundary
- **WHEN** the job completes and delivers results
- **THEN** the delivery uses that persisted originating trust boundary
- **AND** the result is not delivered with a broader boundary than the one
  stored at job creation time

#### Scenario: Submission without a turn source fails loud

- **WHEN** background-job submission is reached without a turn source
- **THEN** the submission throws an explicit exception
- **AND** no job is created with a substituted `Personal` audience or boundary

## ADDED Requirements

### Requirement: Persisted job records carry required trust fields

The persisted `BackgroundJobDefinition` and `ActiveJobInfo` records SHALL
declare their audience and boundary fields as `required` and non-optional, so
that every in-process construction is enforced by the compiler. A legacy
`BackgroundJobDefinition` JSON document that lacks these fields SHALL be
backfilled at load with a conservative fail-closed value — `Public` audience
and the public boundary — and the backfill SHALL be logged at warning level
naming the document. The system SHALL NOT backfill the previous `Personal`
default.

#### Scenario: Legacy job document is backfilled fail-closed at load

- **GIVEN** a persisted `BackgroundJobDefinition` JSON document that predates
  this change and lacks an audience or boundary field
- **WHEN** the job store deserializes it
- **THEN** the missing audience is set to `Public` and the missing boundary to
  the public boundary
- **AND** a warning naming the document is logged
- **AND** no `Personal` audience or boundary is substituted

#### Scenario: Current job documents round-trip unchanged

- **GIVEN** a `BackgroundJobDefinition` written after this change with explicit
  audience and boundary
- **WHEN** the job store deserializes it
- **THEN** the audience and boundary are read verbatim with no warning logged
