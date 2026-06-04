## ADDED Requirements

### Requirement: Mattermost transport lifecycle is actor-owned

Mattermost SHALL manage WebSocket transport lifecycle through an actor-backed
state machine. The lifecycle actor SHALL serialize connect, disconnect,
transport event, health snapshot, and clean reconnect transitions.

The Mattermost routing actor SHALL NOT own transport connect/disconnect state.
`MattermostGatewayActor` SHALL remain responsible for message deduplication,
ACL dispatch, conversation/session actor routing, and HTTP callback interaction
routing.

#### Scenario: Mattermost reports healthy after ready connection

- **GIVEN** Mattermost is enabled with a valid server URL and bot token
- **WHEN** the lifecycle actor resolves bot identity and starts WebSocket
  receiving
- **THEN** `GetSnapshotAsync` reports connected and ready
- **AND** the snapshot includes the Mattermost bot user id and username
- **AND** `MattermostChannel.GetHealthAsync` reports healthy

#### Scenario: Runtime disconnect requests clean reconnect

- **GIVEN** Mattermost is connected and ready
- **WHEN** the transport raises a disconnected event outside an operator stop
- **THEN** the lifecycle actor transitions out of ready
- **AND** the client raises a clean reconnect request
- **AND** health reports disconnected or degraded with the disconnect reason

#### Scenario: Reconnect does not duplicate transport handlers

- **GIVEN** Mattermost has completed one connect, disconnect, and reconnect
  cycle
- **WHEN** the transport raises one message event
- **THEN** Netclaw publishes exactly one `MattermostGatewayMessage`
- **AND** the transport event handlers have not been subscribed more than once

#### Scenario: Ingress is gated while not ready

- **GIVEN** the Mattermost lifecycle actor is disconnected or connecting
- **WHEN** a transport message event arrives
- **THEN** the event is not routed to `MattermostGatewayActor`
- **AND** channel telemetry records a not-ready filtered event

#### Scenario: Graceful stop unsubscribes transport events

- **GIVEN** Mattermost is connected
- **WHEN** the channel stops
- **THEN** transport event handlers are unsubscribed
- **AND** the gateway actor is drained before the transport disconnect completes
