## ADDED Requirements

### Requirement: Channels are output-capable delivery surfaces

Netclaw SHALL define a channel as an addressable delivery surface that can emit
output and MAY also produce input.

Each output-capable channel SHALL expose a descriptor through the channel
registry. The descriptor SHALL declare the stable key, channel type, channel
kind, display name, enabled state, capabilities, supported tool intents, and
supported address namespaces.

Descriptors SHALL NOT grant permissions. ACL decisions SHALL continue to use the
explicit audience, principal, boundary, and provenance carried on `ChannelInput`
and `MessageSource`.

#### Scenario: Output-capable channels are represented

- **GIVEN** the daemon has loaded channel integrations
- **WHEN** the channel registry is enumerated
- **THEN** Slack, Discord, Mattermost, and TUI are represented by descriptors or
  explicit unsupported/not-configured channel records
- **AND** each record declares whether it is a remote chat channel or a local
  interactive channel

#### Scenario: Channels may act as input sources

- **GIVEN** Slack receives a message in a thread
- **WHEN** Slack constructs the session input
- **THEN** the input source identifies Slack channel ingress
- **AND** the input includes a default delivery target for the originating Slack
  channel/thread

#### Scenario: Descriptor capabilities do not bypass ACL

- **GIVEN** a descriptor declares that a channel supports proactive send
- **WHEN** a session turn is authorized
- **THEN** tool access still evaluates the turn's explicit trust context
- **AND** the descriptor capability is not treated as an ACL grant

### Requirement: Trigger sources consume channel delivery targets

Reminders, schedulers, and webhooks SHALL be modeled as trigger sources, not
channels. A trigger source SHALL NOT register a channel descriptor or channel
runtime snapshot provider.

A trigger source that requests external output SHALL provide an explicit
`ChannelDeliveryTarget`. If no delivery target is provided, the turn MAY be
fire-and-forget, but any attempt to emit external output SHALL fail loudly.

#### Scenario: Reminder emits through configured channel target

- **GIVEN** a reminder is configured with a Slack delivery target
- **WHEN** the reminder fires and the session emits output
- **THEN** Netclaw resolves the Slack delivery target through the channel
  registry
- **AND** output is delivered through Slack
- **AND** the reminder is not treated as an output channel

#### Scenario: Webhook emits through configured channel target

- **GIVEN** a GitHub webhook route is configured with a Mattermost delivery
  target
- **WHEN** the webhook receives an event and the session emits output
- **THEN** Netclaw resolves the Mattermost delivery target through the channel
  registry
- **AND** output is delivered through Mattermost
- **AND** the webhook is not treated as an output channel

#### Scenario: Trigger source without target cannot emit external output

- **GIVEN** a webhook route has no delivery target
- **WHEN** the webhook-created session attempts to emit external output
- **THEN** Netclaw fails loudly with a missing delivery target error
- **AND** no default channel is selected

### Requirement: Channel runtime health uses standardized snapshots

Every descriptor-backed output channel SHALL expose a runtime snapshot that
reports enabled state, health status, health detail, connected state when
meaningful, ready state when meaningful, service principal identity when
available, and activity metadata when available.

#### Scenario: Ready remote chat channel reports healthy

- **GIVEN** Slack, Discord, or Mattermost is enabled and ready to receive inbound
  events and send replies
- **WHEN** runtime snapshots are enumerated
- **THEN** the channel snapshot reports enabled and healthy
- **AND** connected and ready are true when those states are meaningful for the
  channel

#### Scenario: Connected but not-ready channel reports degraded

- **GIVEN** a stateful remote chat channel has a socket connection but cannot
  safely route inbound events
- **WHEN** its runtime snapshot is requested
- **THEN** connected is true
- **AND** ready is false
- **AND** health is degraded with a detail explaining the not-ready condition

#### Scenario: Disabled channel reports configured disabled state

- **GIVEN** a channel is disabled by configuration
- **WHEN** its runtime snapshot is requested
- **THEN** enabled is false
- **AND** health reports a disabled or degraded state without attempting a
  transport connection

### Requirement: Channel status and stats are descriptor-driven

Daemon channel runtime status and daemon channel stats SHALL enumerate channel
descriptors and runtime snapshots rather than hard-coding specific channel
adapters. Trigger-source status MAY be reported separately, but SHALL NOT be
merged into channel status as a channel descriptor.

#### Scenario: Newly registered channel appears in status without status-service changes

- **GIVEN** a new output-capable channel registers a descriptor and runtime
  snapshot provider
- **WHEN** daemon runtime status is requested
- **THEN** the channel appears in the channel status collection
- **AND** no channel-specific branch is required in the status service

#### Scenario: Channel activity includes descriptor-backed channels

- **GIVEN** Slack, Discord, and Mattermost have recorded channel activity
- **WHEN** daemon stats are requested
- **THEN** activity for all three channels is included through descriptor-backed
  enumeration

#### Scenario: Trigger source status is separate from channel status

- **GIVEN** reminder scheduling status is available
- **WHEN** daemon runtime status is requested
- **THEN** reminder status MAY appear in a trigger-source or scheduler section
- **AND** it does not appear as a channel descriptor

### Requirement: Channel address resolution accepts IDs and user-facing names

Channel address resolution SHALL use a common resolver contract for supported
address kinds, including users and destinations. Resolvers SHALL accept stable
IDs and user-facing names where the backing channel supports them.

Each descriptor-backed channel SHALL provide its own resolver for the address
namespaces it supports. The daemon SHALL route resolution requests to the
resolver associated with the selected channel descriptor. If no resolver exists
for the requested channel and address kind, resolution SHALL fail loudly as
unsupported.

Resolvers SHALL fail loudly on ambiguous names and unsupported address kinds.
They SHALL NOT silently fall back from one namespace to another.

#### Scenario: Exact stable ID resolves without search ambiguity

- **GIVEN** a send-message tool receives a selected channel descriptor
- **AND** the destination value is a stable platform channel ID for that channel
- **WHEN** the resolver evaluates the destination
- **THEN** it resolves the exact ID without display-name search

#### Scenario: Ambiguous user-facing query fails with candidates

- **GIVEN** the selected channel resolver returns multiple user or destination
  candidates for a user-facing query
- **WHEN** the lookup request requires a single resolved address
- **THEN** resolution fails loudly
- **AND** the result includes candidate stable IDs and display names

#### Scenario: User lookup resolves by display query

- **GIVEN** Slack, Discord, or Mattermost supports user lookup
- **WHEN** an LLM-facing lookup tool searches for a user-facing name
- **THEN** the resolver returns matching users with stable IDs and display data
- **AND** callers can pass the stable ID to send-message or DM-capable tools

### Requirement: LLM-facing channel tools use standard delivery intents

LLM-facing channel tools SHALL map to standard channel delivery intents for send
message, lookup user, and lookup destination. Existing channel-specific tool
names are not compatibility requirements and MAY be renamed during migration.
When tool names change, system skills and eval cases SHALL be updated in the same
implementation change.

#### Scenario: Send-message tools share a common channel target model

- **GIVEN** Slack, Discord, and Mattermost expose send-message tools
- **WHEN** their tool definitions are inspected
- **THEN** each tool accepts a channel key, destination, text, and optional thread
  or root target using the standard send-channel-message intent schema
- **AND** unsupported options are omitted or reported as unsupported rather than
  silently ignored

#### Scenario: Channel-specific tools are renamed to standard tools

- **GIVEN** Slack, Discord, and Mattermost have migrated to the standard channel
  delivery intent model
- **WHEN** LLM-facing channel tools are registered
- **THEN** the registered tool names follow the standardized naming plan
- **AND** obsolete per-channel names are not required as aliases unless a
  concrete external compatibility requirement is documented

### Requirement: Stateful remote chat channels expose reliable lifecycle state

Stateful remote chat channels SHALL expose lifecycle state through their runtime
snapshot and SHALL gate inbound events while not ready. Reconnects SHALL NOT
duplicate transport SDK event handlers. Unexpected disconnects SHALL be reported
as disconnected or degraded state and MAY request a clean reconnect when a full
transport restart is required.

#### Scenario: Not-ready ingress is gated

- **GIVEN** a stateful remote chat channel is disconnected or connecting
- **WHEN** the platform SDK raises an inbound message event
- **THEN** the event is not routed to the session pipeline
- **AND** the channel records or logs that ingress was filtered while not ready

#### Scenario: Reconnect does not duplicate SDK handlers

- **GIVEN** a stateful remote chat channel completes a connect, disconnect, and
  reconnect cycle
- **WHEN** the platform SDK raises one message event
- **THEN** Netclaw publishes exactly one normalized gateway message
- **AND** SDK event handlers have not been subscribed more than once

#### Scenario: Mattermost lifecycle implementation satisfies the common contract

- **GIVEN** Mattermost implements the standardized runtime snapshot contract
- **WHEN** Mattermost is actorized or otherwise given a serialized lifecycle
  owner
- **THEN** it satisfies the same not-ready ingress, disconnect health, clean
  reconnect, and handler de-duplication scenarios as other stateful remote chat
  channels
