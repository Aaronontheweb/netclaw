## ADDED Requirements

### Requirement: Adapters expose standardized channel descriptors

Netclaw SHALL expose a standardized descriptor through a common registry for
every logical conversation source, daemon transport endpoint, internal source,
and HTTP ingress source.

The descriptor SHALL declare the source kind, stable key, channel type when
applicable, display name, enabled state, capabilities, supported tool intents,
and supported address namespaces.

Descriptors SHALL NOT grant permissions. ACL decisions SHALL continue to use the
explicit audience, principal, boundary, and provenance carried on `ChannelInput`
and `MessageSource`.

#### Scenario: Every channel type is represented

- **GIVEN** the daemon has loaded channel integrations
- **WHEN** the channel registry is enumerated
- **THEN** Slack, Discord, Mattermost, TUI, Headless, SignalR, Reminder, and
  Webhook are represented by either a descriptor or an explicit unsupported or
  not-configured record
- **AND** each record declares whether it is a logical channel, daemon endpoint,
  internal source, or HTTP ingress source

#### Scenario: SignalR endpoint is distinct from TUI logical channel

- **GIVEN** the TUI sends messages over the SignalR hub
- **WHEN** descriptors are enumerated
- **THEN** the SignalR hub is represented as a daemon endpoint
- **AND** the TUI is represented as a logical local client channel
- **AND** the TUI descriptor carries session interaction capabilities rather than
  remote workspace capabilities

#### Scenario: Descriptor capabilities do not bypass ACL

- **GIVEN** a descriptor declares that a channel supports proactive send
- **WHEN** a session turn is authorized
- **THEN** tool access still evaluates the turn's explicit trust context
- **AND** the descriptor capability is not treated as an ACL grant

### Requirement: Runtime health uses standardized snapshots

Every descriptor-backed adapter or endpoint SHALL expose a runtime snapshot that
reports enabled state, health status, health detail, connected state when
meaningful, ready state when meaningful, service principal identity when
available, endpoint identity when applicable, and activity metadata when
available.

#### Scenario: Ready remote chat adapter reports healthy

- **GIVEN** Slack, Discord, or Mattermost is enabled and ready to receive inbound
  events and send replies
- **WHEN** runtime snapshots are enumerated
- **THEN** the adapter snapshot reports enabled and healthy
- **AND** connected and ready are true when those states are meaningful for the
  adapter

#### Scenario: Connected but not-ready adapter reports degraded

- **GIVEN** a stateful remote chat adapter has a socket connection but cannot
  safely route inbound events
- **WHEN** its runtime snapshot is requested
- **THEN** connected is true
- **AND** ready is false
- **AND** health is degraded with a detail explaining the not-ready condition

#### Scenario: Disabled adapter reports configured disabled state

- **GIVEN** an adapter is disabled by configuration
- **WHEN** its runtime snapshot is requested
- **THEN** enabled is false
- **AND** health reports a disabled or degraded state without attempting a
  transport connection

### Requirement: Runtime status and stats are descriptor-driven

Daemon runtime status and daemon stats SHALL enumerate channel descriptors and
runtime snapshots rather than hard-coding specific adapters.

#### Scenario: Newly registered channel appears in status without status-service changes

- **GIVEN** a new adapter registers a descriptor and runtime snapshot provider
- **WHEN** daemon runtime status is requested
- **THEN** the adapter appears in the channel or endpoint status collection
- **AND** no adapter-specific branch is required in the status service

#### Scenario: Channel activity includes all descriptor-backed channels

- **GIVEN** Slack, Discord, and Mattermost have recorded channel activity
- **WHEN** daemon stats are requested
- **THEN** activity for all three adapters is included through descriptor-backed
  enumeration

### Requirement: Address resolution accepts IDs and user-facing names

Channel address resolution SHALL use a common resolver contract for supported
address kinds, including users and destinations. Resolvers SHALL accept stable
IDs and user-facing names where the backing platform supports them.

Resolvers SHALL fail loudly on ambiguous names and unsupported address kinds.
They SHALL NOT silently fall back from one namespace to another.

#### Scenario: Exact stable ID resolves without search ambiguity

- **GIVEN** a send-message tool receives a destination value that is a stable
  platform channel ID
- **WHEN** the resolver evaluates the destination
- **THEN** it resolves the exact ID without display-name search

#### Scenario: Ambiguous display name fails with candidates

- **GIVEN** two Mattermost channels have the same display name visible to the bot
- **WHEN** a lookup query uses that display name
- **THEN** resolution fails loudly
- **AND** the result includes candidate stable IDs and display names

#### Scenario: User lookup resolves by display query

- **GIVEN** Slack, Discord, or Mattermost supports user lookup
- **WHEN** an LLM-facing lookup tool searches for a user-facing name
- **THEN** the resolver returns matching users with stable IDs and display data
- **AND** callers can pass the stable ID to send-message or DM-capable tools

### Requirement: LLM-facing channel tools use standard intent schemas

LLM-facing channel tools SHALL map to standard tool intents for send message,
lookup user, and lookup destination. Existing channel-specific tool names MAY
remain during migration, but their arguments and behavior SHALL map to the
standard intent schema.

#### Scenario: Send-message tools share a common argument model

- **GIVEN** Slack, Discord, and Mattermost expose send-message tools
- **WHEN** their tool definitions are inspected
- **THEN** each tool accepts a destination, text, and optional thread or root
  target using the standard send-message intent schema
- **AND** unsupported options are omitted or reported as unsupported rather than
  silently ignored

#### Scenario: Legacy tool name remains as an alias

- **GIVEN** existing sessions know about `send_slack_message`
- **WHEN** Slack tools are registered under the standard intent model
- **THEN** `send_slack_message` remains available as an alias or channel-specific
  registration
- **AND** it maps to the same send-message intent used by other channels

### Requirement: Stateful remote chat adapters expose reliable lifecycle state

Stateful remote chat adapters SHALL expose lifecycle state through their runtime
snapshot and SHALL gate inbound events while not ready. Reconnects SHALL NOT
duplicate transport SDK event handlers. Unexpected disconnects SHALL be reported
as disconnected or degraded state and MAY request a clean reconnect when a full
transport restart is required.

#### Scenario: Not-ready ingress is gated

- **GIVEN** a stateful remote chat adapter is disconnected or connecting
- **WHEN** the platform SDK raises an inbound message event
- **THEN** the event is not routed to the session pipeline
- **AND** the adapter records or logs that ingress was filtered while not ready

#### Scenario: Reconnect does not duplicate SDK handlers

- **GIVEN** a stateful remote chat adapter completes a connect, disconnect, and
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
  adapters
