## ADDED Requirements

### Requirement: Active background jobs defer idle session passivation

Before idle passivation, the session SHALL check its actor-local background-job state. A shell job SHALL count as active only while it lacks a reap timestamp. A reaped job record SHALL NOT block idle passivation. A completed job SHALL release the idle-passivation guard when the session removes its active record.

#### Scenario: Active shell job defers idle passivation

- **GIVEN** a session has a shell background job without a reap timestamp
- **AND** the session is in phase `Ready`
- **WHEN** the idle timeout fires
- **THEN** the session remains active
- **AND** it does not start idle passivation

#### Scenario: Reaped shell job record does not defer idle passivation

- **GIVEN** a session has a shell background job record with a reap timestamp
- **AND** the session is in phase `Ready`
- **WHEN** the idle timeout fires
- **THEN** the job record does not block idle passivation

#### Scenario: Job completion releases the idle-passivation guard

- **GIVEN** a session has an active shell background job
- **WHEN** the session processes the job result and removes its active record
- **THEN** the job no longer blocks idle passivation

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.
