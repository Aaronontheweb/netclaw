## ADDED Requirements

### Requirement: Channel binding lifetime follows session lifetime

Slack, Discord, and Mattermost bindings SHALL keep their session output subscription while the session remains active. A binding SHALL NOT stop on an independent idle timeout. After a binding receives committed `SessionDeactivated`, it SHALL drain its session pipeline and stop. A conversation parent SHALL NOT have an independent idle timeout. It SHALL remain active while a binding child remains and stop after its last binding child terminates.

This requirement does not add input buffering, replay, or a guarantee for input that arrives during pipeline drain. Existing ingress routing and authorization remain unchanged.

#### Scenario: Binding stays active while its session remains active

- **GIVEN** a Slack, Discord, or Mattermost binding has an active session
- **WHEN** the former binding idle timeout would expire
- **THEN** the binding stays active
- **AND** it keeps its session output subscription

#### Scenario: Binding stops after committed session deactivation

- **GIVEN** a binding receives committed `SessionDeactivated`
- **WHEN** its session pipeline drain completes
- **THEN** the binding stops
- **AND** it does not restart its pipeline for that deactivation

#### Scenario: Conversation parent stays alive while a binding exists

- **GIVEN** a conversation parent has a live channel binding child
- **WHEN** the former parent idle period elapses
- **THEN** the parent remains active
- **AND** it does not stop the binding

#### Scenario: Conversation parent stops after its last binding child

- **GIVEN** all binding children have terminated after session deactivation
- **WHEN** the parent handles the last child's termination
- **THEN** the parent stops

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.
