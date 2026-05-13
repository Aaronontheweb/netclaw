## ADDED Requirements

### Requirement: Approval responses route by SessionId, independent of channel-adapter actor liveness

The system SHALL deliver approval responses (Slack `block_actions` payloads,
Discord interaction payloads, and platform text replies that match a pending
approval option) to the originating session actor regardless of whether any
subset of the channel-adapter actor tree is hot at the moment of the
response. Delivery SHALL NOT depend on an in-memory pending-approval lookup
table held by a per-thread or per-channel binding actor; channel-adapter
binding actors SHALL NOT carry routing-relevant approval state.

For button responses, the channel-adapter ingress SHALL decode the platform
payload and the encoded button value into a self-contained protocol message
addressed to the session actor at its deterministic path.

For text responses, the channel-adapter ingress SHALL forward inbound text
unconditionally to the session actor as the existing user-message message
type. The session actor SHALL classify the text at the entry of its
user-message handler: if a pending approval is awaiting decision AND the
text matches one of that pending call's option keys, the session SHALL
treat the text as an approval response and SHALL NOT add it to conversation
history as a user turn; otherwise the text SHALL be processed as a normal
user message exactly as today.

The session actor SHALL be the authority that matches the response to a
pending tool call, runs `CanApprove`, and produces the resulting
`ToolInteractionResponse`.

#### Scenario: Button click delivered when channel-adapter actor tree is cold

- **GIVEN** an approval prompt has been posted in a Slack thread
- **AND** the channel-level Slack adapter actor has subsequently passivated due to channel idle
- **AND** the per-thread binding actor was reaped together with its parent
- **AND** the originating session actor is still alive at its deterministic path
- **WHEN** the user clicks an approval button in the Slack message
- **THEN** the channel-adapter ingress decodes the payload and the encoded button value
- **AND** the response is delivered to the session actor at its deterministic path
- **AND** the session actor resolves the pending tool call

#### Scenario: Text approval reply delivered when binding has been re-spawned

- **GIVEN** an approval prompt has been posted in a Slack thread
- **AND** the per-thread binding actor for that thread has subsequently been stopped and re-spawned
- **AND** the re-spawned binding holds no in-memory approval state
- **WHEN** the user replies with a single character matching one of the pending approval's option keys
- **THEN** the binding forwards the text unconditionally to the session actor
- **AND** the session actor classifies the text as the matching approval response
- **AND** the session actor resolves the pending tool call
- **AND** the text is not added to conversation history as a user turn

#### Scenario: Authorization check runs at the session actor

- **GIVEN** an approval prompt has been posted by user `U_requester`
- **AND** a different user `U_approver` clicks an approval button or types an approval letter
- **WHEN** the response is delivered to the session actor
- **THEN** `CanApprove(requesterPrincipal, requesterSenderId, approvingSenderId)` is evaluated at the session actor
- **AND** if the check fails, no `ToolInteractionResponse` is produced and the pending tool call remains pending
- **AND** the authorization decision matches what `ApprovalButtonValueCodec.CanApprove` would have decided for the same inputs

#### Scenario: Unknown callId on session actor is ignored, not crashed

- **GIVEN** a button click is delivered to the session actor with a `callId` that does not match any pending tool call
- **WHEN** the session actor processes the response
- **THEN** the session actor logs the unknown callId and discards the response
- **AND** the session actor does not crash
- **AND** the session actor's pending-call set is unchanged

#### Scenario: Text without pending approval falls through as normal user message

- **GIVEN** the session has no pending approval awaiting decision
- **WHEN** the user types a single character that would otherwise match an approval option key (e.g., `A`, `B`, `C`, `D`)
- **THEN** the session actor processes the text as a normal user message
- **AND** the text is added to conversation history as a user turn
- **AND** no approval-resolution side effects occur

#### Scenario: Resolved-state UI redraw does not depend on prior binding state

- **GIVEN** an approval response has been resolved at the session actor
- **AND** the per-thread binding actor was cold prior to the response arriving
- **WHEN** the session actor emits the resolved-state redraw command
- **THEN** the redraw command is self-contained (carries the channel, message identifier, original tool request, selected decision, and approver)
- **AND** the binding actor produces the new platform message from those inputs alone
- **AND** the binding does not consult any prior in-memory state to render the resolved blocks
