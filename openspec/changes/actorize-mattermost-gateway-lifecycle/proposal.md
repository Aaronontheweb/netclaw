## Why

Mattermost channel reliability is currently split between `MattermostChannel`
and `MattermostNetGatewayClient`. Reconnects are coordinated by a hosted-service
background task, while the transport client subscribes to Mattermost.NET SDK
events inside `ConnectAsync` and unsubscribes only on dispose. A reconnect can
therefore duplicate SDK event handlers, and runtime disconnect events are logged
without becoming lifecycle state that can drive health or recovery.

Discord already uses an actor-backed lifecycle state machine behind its gateway
client. Mattermost should use the same reliability pattern for connection state,
without moving message routing into the transport lifecycle actor.

Source PRDs/specs: `PRD-009-input-adapters-and-unified-input.md`,
`SPEC-011-daemon-architecture.md`, `openspec/specs/netclaw-input-adapters/spec.md`.

## What Changes

- Add a `MattermostNetGatewayLifecycleActor` that owns Mattermost WebSocket
  connection state, SDK event subscriptions, bot identity, health snapshots, and
  clean-reconnect requests.
- Change `MattermostNetGatewayClient` into an actor-backed facade, matching the
  Discord transport shape.
- Change `MattermostChannel.GetHealthAsync` to use lifecycle snapshots instead
  of a raw `IsConnected` boolean.
- Keep `MattermostChannel` responsible for hosted-service startup/shutdown,
  fatal configuration containment, gateway actor registration, and bounded
  reconnect backoff.
- Keep `MattermostGatewayActor` responsible for message deduplication, ACL
  dispatch, conversation/session routing, and HTTP callback interactions.

## Capabilities

### New Capabilities

<!-- None. This extends the existing input-adapter lifecycle contract. -->

### Modified Capabilities

- `netclaw-input-adapters`: Add requirements for actor-owned Mattermost
  transport lifecycle state, snapshot-based health, clean reconnect signaling,
  ingress gating while not ready, and non-duplicated SDK event subscriptions.

## Impact

- **Affected systems:** Mattermost transport client, Mattermost channel
  hosted-service lifecycle, channel health reporting, Mattermost transport tests.
- **Security:** no new inbound surface and no ACL bypass. The Mattermost action
  callback endpoint continues to route through `MattermostGatewayActor`.
- **Reliability:** runtime disconnects become explicit state transitions,
  reconnects do not multiply SDK event handlers, and health reports become
  state-machine snapshots instead of a transport boolean.
- **Compatibility:** no configuration schema change, no session identity change,
  no change to Slack or Discord behavior.
