## MODIFIED Requirements

### Requirement: Discord interactive approval with deterministic text fallback

The Discord adapter SHALL handle `ToolInteractionRequest` in Discord sessions by
preferring Discord interaction controls when available and SHALL always support
deterministic text fallback with equivalent approval options and outcomes.

Inbound delivery of approval responses (interaction click and text fallback
alike) SHALL route to the originating session actor without depending on the
`DiscordSessionBindingActor` being hot at the moment of the response.
Classification of inbound text as approval-vs-normal SHALL be performed by
the session actor, not by the binding.

#### Scenario: Discord interaction approval path succeeds

- **GIVEN** Discord interaction callbacks are available
- **WHEN** a tool approval request is emitted
- **THEN** the adapter renders interaction controls
- **AND** selected approval decision is routed to the session actor at its deterministic path

#### Scenario: Interaction path unavailable falls back to text deterministically

- **GIVEN** Discord interaction callbacks are unavailable or fail
- **WHEN** a tool approval request is emitted
- **THEN** the adapter emits a text prompt with deterministic A/B/C/D options
- **AND** subsequent inbound text is forwarded unconditionally to the session actor
- **AND** the session classifies matching letters as the equivalent approval response
- **AND** non-matching text is processed as a normal user message

## ADDED Requirements

### Requirement: Discord button approval response routing independent of session binding liveness

The Discord adapter SHALL deliver Discord interaction payloads for approval
buttons to the originating session actor without depending on the
`DiscordSessionBindingActor` being alive at the moment of the click. The
Discord ingress (`DiscordConversationActor` or its equivalent) SHALL decode
the interaction payload and the `ApprovalButtonValueCodec`-encoded
`custom_id`, construct a self-contained protocol message containing the
resolved `SessionId`, `callId`, `optionKey`, approver sender ID, channel ID,
approval-message identifier, and any platform-supplied response token, and
address the message to the session actor at its deterministic path. The
session actor SHALL be the authority that runs `CanApprove`, matches the
`callId` against its pending-call set (with prefix-match tolerance), and
produces the resulting `ToolInteractionResponse`. The Discord ingress SHALL
NOT consult any in-memory pending-approval map held by
`DiscordSessionBindingActor` to perform this routing.

This requirement complements the modified `Discord interactive approval
with deterministic text fallback` requirement above: that requirement
defines the user-visible flows; this requirement defines the routing
contract for click-style responses.

#### Scenario: Button click delivered after Discord session binding has stopped

- **GIVEN** an approval prompt has been posted in a Discord channel by the Discord adapter
- **AND** the `DiscordSessionBindingActor` for that session has subsequently stopped or been passivated
- **AND** the originating session actor remains alive
- **WHEN** the user clicks an approval button in the Discord message
- **THEN** Discord delivers the interaction payload to the daemon
- **AND** the Discord ingress decodes the payload and the encoded `custom_id`
- **AND** the response is delivered to the session actor at its deterministic path
- **AND** the session actor resolves the pending tool call

#### Scenario: Discord text fallback delivered after binding re-spawned cold

- **GIVEN** a text-fallback approval prompt has been posted in a Discord channel
- **AND** the `DiscordSessionBindingActor` has been stopped and re-spawned
- **AND** the re-spawned binding holds no in-memory approval state
- **WHEN** the user replies with a single approval option character
- **THEN** the binding forwards the text unconditionally to the session
- **AND** the session resolves the pending approval correctly

#### Scenario: Resolved-state redraw is issued from session actor with self-contained inputs

- **GIVEN** the session actor has resolved a Discord button-click approval response
- **WHEN** the session actor issues the resolved-state redraw
- **THEN** the redraw command carries the channel ID, approval-message identifier, original `ToolInteractionRequest`, selected option key, and approver sender ID
- **AND** the Discord output binding renders the resolved message from those inputs alone
- **AND** the binding does not consult any prior in-memory pending-approval map to render the resolved message
