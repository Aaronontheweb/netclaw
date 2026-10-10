## ADDED Requirements

### Requirement: Channel bindings retire with the session

Slack, Discord, and Mattermost bindings SHALL keep their session output subscription until the session commits to stop. A binding SHALL NOT stop on its own idle timer while its session remains active. A binding SHALL drain its input pipeline after it receives the committed deactivation event, then stop through its conversation parent. The parent SHALL apply its current ingress ACL and routing checks before it queues new input. During graceful retirement, the parent SHALL preserve authorized deliveries that it queues or that the old binding returns after deactivation. It SHALL preserve each delivery's payload, `Principal`, `Audience`, `Provenance`, and original reply target. It SHALL recreate the binding after the old binding stops when a delivery waits or an authorized binding-ensure request arrived during retirement. It SHALL deliver each queued delivery once in order. The binding SHALL retain its current per-command authorization checks. A conversation parent SHALL defer idle stop while any binding child remains. This requirement does not guarantee admission for input that the old binding already submitted to its local pipeline queue.

#### Scenario: Input that arrives during drain reaches the replacement binding once

- **GIVEN** a channel binding drains after session deactivation
- **WHEN** the parent receives authorized input during retirement
- **THEN** the parent retains the input and its original reply target
- **AND** creates a replacement after the old binding stops
- **AND** delivers the input once to the replacement

#### Scenario: Mailbox input order survives the retirement barrier

- **GIVEN** the parent routed input to the old binding before it received the retirement notice
- **AND** that input remains in the old binding mailbox when deactivation begins
- **AND** the parent received more input after that notice
- **WHEN** the old binding drains and stops
- **THEN** the replacement receives the earlier input before later input
- **AND** each input retains its original reply target

#### Scenario: The parent rejects unauthorized input during retirement

- **GIVEN** a sender lacks permission to use a channel session
- **WHEN** that sender submits input while the binding retires
- **THEN** the parent rejects the input under the existing ingress ACL
- **AND** it does not queue or admit the input

#### Scenario: The binding keeps its per-command authorization check

- **GIVEN** an approval response names a different requester
- **WHEN** the replacement binding handles the response
- **THEN** the binding rejects it under the existing requester check

#### Scenario: Conversation parent stays alive while a binding exists

- **GIVEN** a conversation parent has a live channel binding child
- **WHEN** its idle timeout fires
- **THEN** the parent remains active
- **AND** it does not stop the binding

#### Scenario: Conversation parent stops after all bindings stop

- **GIVEN** a conversation parent has no binding children
- **WHEN** its idle timeout fires
- **THEN** the parent can stop

#### Scenario: Authorized ensure request recreates a binding after retirement

- **GIVEN** Mattermost accepts an authorized proactive-thread setup while its binding retires
- **AND** no queued delivery waits
- **WHEN** the old binding stops
- **THEN** the conversation parent creates a replacement binding for that setup

#### Scenario: Local pipeline queue admission is outside graceful replay

- **GIVEN** the old binding already wrote input to its local pipeline queue
- **WHEN** the session commits to stop before the session actor confirms admission
- **THEN** the retirement protocol makes no durable-admission claim for that input

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.
