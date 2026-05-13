## ADDED Requirements

### Requirement: Button approval responses route independently of channel-adapter actor liveness

The system SHALL deliver button-click approval responses (Slack `block_actions`
payloads and Discord interaction payloads) to the originating session actor
regardless of whether any subset of the channel-adapter actor tree is hot at
the moment of the click. Delivery SHALL NOT depend on an in-memory
pending-approval lookup table held by a per-thread or per-channel binding
actor. The channel-adapter ingress SHALL decode the platform payload and the
encoded button value, construct a self-contained protocol message, and address
it to the session actor at its deterministic path. The session actor SHALL be
the authority that matches the response to a pending tool call, runs
`CanApprove`, and produces the resulting `ToolInteractionResponse`.

This requirement applies to **button** approval responses only. Text-reply
approval routing (where the user replies with `A`/`B`/`C`/`D`) is unchanged
and continues to require pending-state lookup at the binding actor.

#### Scenario: Approval click delivered when channel-level adapter actor is cold

- **GIVEN** an approval prompt has been posted in a Slack thread
- **AND** the channel-level Slack adapter actor has subsequently passivated due to channel idle
- **AND** the per-thread binding actor was reaped together with its parent
- **AND** the originating session actor is still alive at its deterministic path
- **WHEN** the user clicks an approval button in the Slack message
- **THEN** the channel-adapter ingress decodes the payload and the encoded button value
- **AND** the response is delivered to the session actor at its deterministic path
- **AND** the session actor resolves the pending tool call

#### Scenario: Approval click delivered when per-thread binding is cold but channel-level adapter is hot

- **GIVEN** an approval prompt has been posted in a Slack thread
- **AND** the per-thread binding actor for that thread has been individually stopped or restarted
- **AND** the channel-level adapter actor remains alive
- **WHEN** the user clicks an approval button
- **THEN** the channel-adapter ingress delivers the response to the session actor without consulting the per-thread binding
- **AND** the session actor resolves the pending tool call

#### Scenario: Authorization check runs at the session actor

- **GIVEN** an approval prompt has been posted in a Slack thread by user `U_requester`
- **AND** a different user `U_approver` clicks an approval button
- **WHEN** the click is delivered to the session actor
- **THEN** `CanApprove(requesterPrincipal, requesterSenderId, approvingSenderId)` is evaluated at the session actor
- **AND** if the check fails, no `ToolInteractionResponse` is produced and the pending tool call remains pending
- **AND** the authorization decision matches what `ApprovalButtonValueCodec.CanApprove` would have decided for the same inputs

#### Scenario: Unknown callId on session actor is ignored, not crashed

- **GIVEN** a button click is delivered to the session actor with a `callId` that does not match any pending tool call
- **WHEN** the session actor processes the response
- **THEN** the session actor logs the unknown callId and discards the response
- **AND** the session actor does not crash
- **AND** the session actor's pending-call set is unchanged

#### Scenario: Resolved-state UI redraw does not depend on prior binding state

- **GIVEN** an approval response has been resolved at the session actor
- **AND** the per-thread binding actor was cold prior to the click
- **WHEN** the session actor emits the resolved-state redraw command
- **THEN** the redraw command is self-contained (carries the channel, message timestamp, original tool request, selected decision, and approver)
- **AND** the binding actor produces the new platform message from those inputs alone
- **AND** the binding does not consult any prior in-memory state to render the resolved blocks
