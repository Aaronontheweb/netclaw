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

This requirement complements the existing `Discord interactive approval with
deterministic text fallback` requirement: that requirement defines what
decision each click produces; this requirement defines how the click is
delivered to the session.

#### Scenario: Button click delivered after Discord session binding has stopped

- **GIVEN** an approval prompt has been posted in a Discord channel by the Discord adapter
- **AND** the `DiscordSessionBindingActor` for that session has subsequently stopped or been passivated
- **AND** the originating session actor remains alive
- **WHEN** the user clicks an approval button in the Discord message
- **THEN** Discord delivers the interaction payload to the daemon
- **AND** the Discord ingress decodes the payload and the encoded `custom_id`
- **AND** the response is delivered to the session actor at its deterministic path
- **AND** the session actor resolves the pending tool call

#### Scenario: Discord button text fallback path is unchanged

- **GIVEN** Discord interaction callbacks are unavailable or fail
- **AND** the adapter has emitted a text prompt with deterministic A/B/C/D options
- **WHEN** a user sends a text reply matching one of the options
- **THEN** the text reply continues to be parsed against the pending approval state held by `DiscordSessionBindingActor`
- **AND** the resulting `ToolInteractionResponse` matches the existing text-fallback semantics

#### Scenario: Resolved-state redraw is issued from session actor with self-contained inputs

- **GIVEN** the session actor has resolved a Discord button-click approval response
- **WHEN** the session actor issues the resolved-state redraw
- **THEN** the redraw command carries the channel ID, approval-message identifier, original `ToolInteractionRequest`, selected option key, and approver sender ID
- **AND** the Discord output binding renders the resolved message from those inputs alone
- **AND** the binding does not consult any prior in-memory pending-approval map to render the resolved message
