## MODIFIED Requirements

### Requirement: Slack text approval reply routing to session

The Slack channel SHALL route inbound text from a Slack thread to the
originating session actor unconditionally, using the existing user-message
message type. The Slack channel SHALL NOT classify text as approval-vs-
normal; classification is the session actor's responsibility. The session
actor SHALL inspect inbound text at the entry of its user-message handler:
if a pending approval is awaiting decision in that session AND the text
matches one of the pending call's approval option keys (`A`/`B`/`C`/`D` or
whatever the call advertised), the session SHALL produce the corresponding
`ToolInteractionResponse` and SHALL NOT add the text to conversation
history as a user turn; otherwise the text SHALL be processed as a normal
user message.

This requirement supersedes the previous routing model in which the
`SlackThreadBindingActor` consulted `_pendingApprovalRequests` to perform
classification. The binding's `_pendingApprovalRequests` field is removed
entirely as part of this change.

#### Scenario: User replies Approve Once

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies `A`
- **THEN** the Slack adapter forwards the text unconditionally to the session actor
- **AND** the session classifies the text against its pending-call set
- **AND** the session produces a `ToolInteractionResponse` with `ApprovedOnce`

#### Scenario: User replies Approve For This Chat

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies `B`
- **THEN** the session produces a `ToolInteractionResponse` with `ApprovedSession`
- **AND** the approval is retained only for the current Slack thread session

#### Scenario: User replies Approve Always

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies `C`
- **THEN** the session produces a `ToolInteractionResponse` with `ApprovedAlways`
- **AND** the approval is persisted to `tool-approvals.json`

#### Scenario: User replies Deny

- **GIVEN** an approval prompt is displayed
- **WHEN** the user replies `D`
- **THEN** the session produces a `ToolInteractionResponse` with `Denied`
- **AND** the tool receives a denial result

#### Scenario: No pending approval means reply falls through as normal message

- **GIVEN** no approval request is pending for the Slack thread
- **WHEN** a user sends `A`, `B`, `C`, or `D`
- **THEN** the session classifies the text as a normal user message
- **AND** the text enters conversation history as a user turn

#### Scenario: Text reply delivered when binding has been re-spawned cold

- **GIVEN** an approval prompt is displayed in a Slack thread
- **AND** the per-thread `SlackThreadBindingActor` has been stopped and re-spawned
- **AND** the re-spawned binding holds no in-memory approval state
- **WHEN** the user replies with a single approval option character
- **THEN** the binding forwards the text unconditionally to the session
- **AND** the session resolves the pending approval correctly

## ADDED Requirements

### Requirement: Slack button approval response routing independent of thread binding liveness

The Slack adapter SHALL deliver Slack `block_actions` payloads for approval
buttons to the originating session actor without depending on the per-thread
`SlackThreadBindingActor` being alive at the moment of the click. The Slack
ingress (`SlackConversationActor` or its equivalent) SHALL decode the
`block_actions` payload and the `ApprovalButtonValueCodec`-encoded button
value, construct a self-contained protocol message containing the resolved
`SessionId`, `callId`, `optionKey`, approver sender ID, channel ID,
approval-message timestamp, and Slack `response_url`, and address the message
to the session actor at its deterministic path. The session actor SHALL be
the authority that runs `CanApprove`, matches the `callId` against its
pending-call set (with prefix-match tolerance), and produces the resulting
`ToolInteractionResponse`. The Slack ingress SHALL NOT consult any in-memory
pending-approval map held by `SlackThreadBindingActor` to perform this routing.

#### Scenario: Button click delivered after channel-level adapter passivated

- **GIVEN** an approval prompt has been posted in a Slack thread by the Slack adapter
- **AND** the channel-level Slack conversation actor has passivated due to channel idle
- **AND** the per-thread binding actor was reaped together with its parent
- **AND** the originating session actor remains alive
- **WHEN** the user clicks an approval button in the Slack thread
- **THEN** Slack delivers the `block_actions` payload to the daemon
- **AND** the Slack ingress decodes the payload and the encoded button value
- **AND** the response is delivered to the session actor at its deterministic path
- **AND** the session actor resolves the pending tool call

#### Scenario: Slack ingress does not log "Ignoring Slack approval response for missing thread" under normal operation

- **GIVEN** an approval prompt has been posted in a Slack thread
- **AND** the originating session actor is alive
- **WHEN** the user clicks an approval button at any later time
- **THEN** the response is routed to the session actor regardless of which channel-adapter actors are currently hot
- **AND** the legacy log line `Ignoring Slack approval response for missing thread` is not emitted

#### Scenario: Resolved-state redraw is issued from session actor with self-contained inputs

- **GIVEN** the session actor has resolved a button-click approval response
- **WHEN** the session actor issues the resolved-state redraw
- **THEN** the redraw command carries the channel ID, approval-message timestamp, original `ToolInteractionRequest`, selected option key, and approver sender ID
- **AND** the Slack output binding renders the resolved blocks from those inputs alone
- **AND** the binding does not consult any prior in-memory pending-approval map to render the new blocks
