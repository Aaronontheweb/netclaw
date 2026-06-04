## 1. OpenSpec planning artifacts

- [ ] 1.1 Confirm proposal, design, and spec delta cover actor-owned Mattermost lifecycle state, clean reconnect signaling, health snapshots, ingress gating, and handler de-duplication.
- [ ] 1.2 Run `openspec validate actorize-mattermost-gateway-lifecycle --type change` and resolve all issues.

## 2. Transport contracts

- [ ] 2.1 Add `MattermostGatewaySnapshot` with connected, ready, health detail, bot user id, and bot username.
- [ ] 2.2 Add `GetSnapshotAsync` to `IMattermostGatewayClient`.
- [ ] 2.3 Add `CleanReconnectRequired` to `IMattermostGatewayClient`.
- [ ] 2.4 Add `IMattermostGatewayTransport` for Mattermost.NET event and start/stop operations.
- [ ] 2.5 Add a Mattermost.NET transport adapter implementing `IMattermostGatewayTransport`.

## 3. Lifecycle actor

- [ ] 3.1 Add `MattermostNetGatewayLifecycleActor` with disconnected, connecting, ready, clean-reconnect-required, disconnecting, and fatal-offline states.
- [ ] 3.2 Move Mattermost.NET event subscription to actor `PreStart`.
- [ ] 3.3 Move Mattermost.NET event unsubscription to actor `PostStop`.
- [ ] 3.4 Resolve bot identity during connect and include it in snapshots.
- [ ] 3.5 Drop or filter transport ingress while not ready and record telemetry.
- [ ] 3.6 Emit clean reconnect requests when the transport disconnects unexpectedly.

## 4. Client and channel migration

- [ ] 4.1 Change `MattermostNetGatewayClient` into an actor-backed facade.
- [ ] 4.2 Update `MattermostChannel.TryConnectAsync` to require a ready snapshot before creating the gateway actor.
- [ ] 4.3 Update `MattermostChannel.GetHealthAsync` to use lifecycle snapshots.
- [ ] 4.4 Update `MattermostChannel` to subscribe to clean reconnect requests and trigger an immediate reconnect loop.
- [ ] 4.5 Preserve `MattermostGatewayActor` construction and actor-registry registration.
- [ ] 4.6 Preserve `/api/mattermost/actions` callback routing through `MattermostGatewayActor`.

## 5. Tests

- [ ] 5.1 Add lifecycle actor connect-success test.
- [ ] 5.2 Add lifecycle actor transient-failure test.
- [ ] 5.3 Add lifecycle actor fatal-failure test.
- [ ] 5.4 Add runtime disconnect test proving health updates and clean reconnect is requested.
- [ ] 5.5 Add reconnect test proving SDK event handlers are not duplicated.
- [ ] 5.6 Add ingress-not-ready test proving messages are not routed and telemetry is recorded.
- [ ] 5.7 Add channel-health tests for healthy, degraded, disconnected, and disabled states.
- [ ] 5.8 Re-run existing Mattermost channel contract tests.

## 6. Validation and quality gates

- [ ] 6.1 `dotnet test src/Netclaw.Actors.Tests/ --filter Mattermost`
- [ ] 6.2 `dotnet test src/Netclaw.Daemon.Tests/`
- [ ] 6.3 `dotnet slopwatch analyze`
- [ ] 6.4 `./scripts/Add-FileHeaders.ps1 -Verify`
