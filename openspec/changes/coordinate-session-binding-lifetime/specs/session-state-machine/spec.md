## REMOVED Requirements

### Requirement: Passivating behavior

**Reason**: The old requirement made subscriber presence control idle passivation.
The session now uses its phase and active-work state.

**Migration**: Keep the existing passivation sequence and idle timer. Apply the
new active-work eligibility rule and emit deactivation only after the session
commits to stop.

## ADDED Requirements

### Requirement: Idle passivation follows active work

The session actor SHALL enter `Passivating` when the existing idle timeout fires, the phase is `Ready`, and the actor-local active-work check returns false. The default idle timeout SHALL be one hour. Subscriber count and journaled approval state SHALL NOT change idle-passivation eligibility. Active-work rules are defined by the `background-job-execution` capability. In `Passivating`, the actor SHALL request final memory distillation from the observer actor, if present, wait up to 5 seconds, save a snapshot, notify the lifecycle observer, and stop itself. Idle-driven passivation SHALL include a short post-snapshot grace window where racing input can abort the stop and return the actor to `Ready`. The actor SHALL emit the session-deactivation output only after it commits to stop; it SHALL NOT emit that output when passivation can still abort.

#### Scenario: Idle timeout triggers passivation when no work remains

- **GIVEN** the session actor is in phase `Ready` with no subscribers and no active work
- **AND** the session uses the default idle timeout
- **WHEN** one hour of idle time elapses
- **THEN** the actor enters `Passivating`
- **AND** it requests final memory distillation from the observer actor, if present

#### Scenario: Active work defers idle passivation

- **GIVEN** the session actor is in phase `Ready` with subscribers and active work
- **WHEN** the idle timeout fires
- **THEN** the actor remains in phase `Ready` because active work remains
- **AND** it does not emit the session-deactivation output

#### Scenario: Subscriber presence does not defer idle passivation

- **GIVEN** the session actor is in phase `Ready` with subscribers and no active work
- **WHEN** the one-hour idle timeout fires
- **THEN** the actor enters `Passivating`

#### Scenario: Shell or foreground work prevents deactivation

- **GIVEN** the session is in phase `Ready` with active background work
- **WHEN** the idle timeout fires
- **THEN** the actor remains in phase `Ready`
- **AND** it does not emit the session-deactivation output

#### Scenario: Passivation completes after distillation

- **GIVEN** the session is in phase `Passivating`
- **WHEN** `SessionDistillationCompleted` arrives from the observer
- **THEN** the actor saves a snapshot
- **AND** notifies the lifecycle observer of deactivation
- **AND** emits the session-deactivation output once
- **AND** stops itself

#### Scenario: Passivation completes on timeout

- **GIVEN** the session is in phase `Passivating`
- **WHEN** 5 seconds elapse without `SessionDistillationCompleted`
- **THEN** the actor saves a snapshot and stops itself
- **AND** it does not wait indefinitely for the observer
- **AND** it emits the session-deactivation output once

#### Scenario: Passivation without observer actor

- **GIVEN** the session has no observer actor
- **AND** the session is idle and has no active work
- **WHEN** the idle timeout fires
- **THEN** the actor saves a snapshot and stops itself
- **AND** it does not request final distillation
- **AND** it emits the session-deactivation output once

#### Scenario: Racing input aborts passivation before commit

- **GIVEN** the session is in `Passivating` during the post-snapshot grace window
- **WHEN** a racing input aborts the stop
- **THEN** the actor returns to `Ready`
- **AND** it does not emit the session-deactivation output

#### Scenario: Messages buffered during passivation

- **GIVEN** the session actor is in phase `Passivating` during its grace window
- **WHEN** a `SendUserMessage` arrives
- **THEN** idle passivation is aborted
- **AND** the actor transitions to `Ready`
- **AND** it handles the message

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.
