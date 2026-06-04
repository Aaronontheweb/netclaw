## 1. OpenSpec planning artifacts

- [ ] 1.1 Confirm proposal, design, and spec delta define channels as output-capable delivery surfaces that may also produce input.
- [ ] 1.2 Confirm reminders and webhooks are represented as trigger consumers of channel delivery targets, not channel registry participants.
- [ ] 1.3 Confirm Mattermost actorization is represented as an adapter-specific lifecycle task, not the top-level change.
- [ ] 1.4 Run `openspec validate standardize-channel-delivery-contracts --type change` and resolve all issues.

## 2. Channel descriptor and snapshot contracts

- [ ] 2.1 Add a standard channel descriptor model for output-capable remote chat and local interactive channels.
- [ ] 2.2 Add capability flags for receive, send, DM, threaded conversations, interactive approval, file ingress, file egress, proactive send, user lookup, destination lookup, and runtime health.
- [ ] 2.3 Add a standard channel runtime snapshot model with enabled, health, connected, ready, principal identity, and activity metadata.
- [ ] 2.4 Add a channel registry service that enumerates descriptor and snapshot providers for output-capable channels only.

## 3. Delivery target contracts

- [ ] 3.1 Add `ChannelDeliveryTarget` with channel key, resolved destination, and optional thread/root target.
- [ ] 3.2 Preserve channel-originated default delivery targets for Slack, Discord, Mattermost, and TUI input turns.
- [ ] 3.3 Require trigger-originated turns to carry an explicit delivery target when external output is requested.
- [ ] 3.4 Fail loudly when a trigger-originated turn attempts external output without a delivery target.

## 4. Existing channel coverage

- [ ] 4.1 Register channel descriptors for Slack, Discord, Mattermost, and TUI or explicitly mark unsupported/not-configured output channels.
- [ ] 4.2 Adapt Slack runtime health to the standard snapshot shape without changing Slack behavior.
- [ ] 4.3 Adapt Discord runtime health to the standard snapshot shape without changing Discord behavior.
- [ ] 4.4 Adapt Mattermost runtime health to the standard snapshot shape without actorizing it yet.
- [ ] 4.5 Represent TUI as a local interactive channel and SignalR as daemon infrastructure, not as the same channel record.

## 5. Trigger-source consumers

- [ ] 5.1 Update reminder definitions to store or resolve explicit channel delivery targets when output is requested.
- [ ] 5.2 Update webhook route definitions to store or resolve explicit channel delivery targets when output is requested.
- [ ] 5.3 Ensure reminders and webhooks do not register channel descriptors or channel snapshot providers.

## 6. Descriptor-driven observability

- [ ] 6.1 Change daemon runtime status to enumerate the channel registry instead of hard-coding individual channel adapters.
- [ ] 6.2 Change daemon stats channel activity to enumerate descriptor-backed output channels.
- [ ] 6.3 Keep trigger-source status separate from channel status when reminder or webhook operational state is reported.
- [ ] 6.4 Preserve current status/stats output fields or provide explicit compatibility mapping.

## 7. Address resolution

- [ ] 7.1 Add a standard channel address resolver contract for users and destinations.
- [ ] 7.2 Support exact stable ID resolution before name search.
- [ ] 7.3 Fail loudly with candidates for ambiguous display-name matches.
- [ ] 7.4 Route resolution requests to the resolver registered for the selected channel descriptor.
- [ ] 7.5 Wire Slack lookup to its channel-scoped resolver.
- [ ] 7.6 Wire Discord lookup to its channel-scoped resolver where supported.
- [ ] 7.7 Wire Mattermost lookup to its channel-scoped resolver.

## 8. LLM-facing channel tool standardization

- [ ] 8.1 Define standard tool intent schemas and final tool names for send channel message, lookup channel user, and lookup channel destination.
- [ ] 8.2 Rename/map existing Slack tools to the standard tool names and intent schema.
- [ ] 8.3 Rename/map existing Discord tools to the standard tool names and intent schema.
- [ ] 8.4 Rename/map existing Mattermost tools to the standard tool names and intent schema.
- [ ] 8.5 Update system skills, CLI/help text, and eval cases for renamed LLM-facing channel tools.

## 9. Stateful channel lifecycle follow-up

- [ ] 9.1 Add contract tests for not-ready ingress gating, runtime disconnect health, clean reconnect signaling, and handler de-duplication for stateful remote chat channels.
- [ ] 9.2 Implement Mattermost lifecycle actorization only after the standard snapshot and lifecycle contract tests exist.
- [ ] 9.3 Verify Slack and Discord satisfy the same lifecycle requirements or document explicit capability differences.

## 10. Validation and quality gates

- [ ] 10.1 `dotnet test src/Netclaw.Actors.Tests/ --filter Channel`
- [ ] 10.2 `dotnet test src/Netclaw.Daemon.Tests/`
- [ ] 10.3 `dotnet slopwatch analyze`
- [ ] 10.4 `./scripts/Add-FileHeaders.ps1 -Verify`
