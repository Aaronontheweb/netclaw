## REMOVED Requirements

### Requirement: Idle passivation proceeds with pending approvals

**Reason**: The old requirement also made live subscribers block idle passivation.
The session now uses its phase and active-work state.

**Migration**: Keep the existing approval journal and response route. An
outstanding approval alone still permits idle passivation.

## ADDED Requirements

### Requirement: Pending approvals remain recoverable after passivation

A session SHALL NOT defer idle passivation because tool approval prompts are outstanding. Pending approval state is journaled (`ToolApprovalRequested` / `ToolApprovalResolved`), and an approval response SHALL rehydrate a passivated session and resume the original turn. A live subscriber SHALL NOT defer idle passivation by itself. The session SHALL preserve the current resolved-approval abandonment behavior for a parked tool batch whose approval was granted but whose tool result never completed. An approval prompt must reach its channel while active work and the binding keep the session available; a click on a prompt that already reached the user can rehydrate the session after passivation.

#### Scenario: Session passivates with an approval prompt outstanding

- **GIVEN** a session is idle past its idle timeout
- **AND** a tool approval prompt is outstanding
- **WHEN** the idle timeout fires
- **THEN** the session passivates normally
- **AND** the pending approval remains recoverable from the journal

#### Scenario: Approval click after passivation resumes the turn

- **GIVEN** a session passivated with an approval prompt outstanding
- **WHEN** the user responds to the approval prompt
- **THEN** the session rehydrates from the journal
- **AND** re-drives the parked tool batch under the restored-approval requirements

#### Scenario: Active work defers passivation

- **GIVEN** a session is idle past its idle timeout
- **AND** a live CLI, TUI, or channel subscriber is attached
- **AND** active background work remains
- **WHEN** the idle timeout fires
- **THEN** active work defers passivation

#### Scenario: A live subscriber does not defer passivation

- **GIVEN** a session is idle past its idle timeout
- **AND** a live CLI, TUI, or channel subscriber is attached
- **AND** no active work remains
- **WHEN** the idle timeout fires
- **THEN** the session passivates normally
- **AND** a later input can rehydrate the session and attach a new subscriber

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.
