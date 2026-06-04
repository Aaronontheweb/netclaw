## 1. OpenSpec planning artifacts

- [ ] 1.1 Confirm proposal, design, and spec delta cover Slack, Discord, Mattermost, TUI, Headless, SignalR, Reminder, Webhook, and future adapters.
- [ ] 1.2 Confirm Mattermost actorization is represented as an adapter-specific lifecycle task, not the top-level change.
- [ ] 1.3 Run `openspec validate standardize-channel-transport-contracts --type change` and resolve all issues.

## 2. Descriptor and snapshot contracts

- [ ] 2.1 Add a standard channel descriptor model for logical channels, daemon endpoints, internal sources, and HTTP ingress sources.
- [ ] 2.2 Add capability flags for receive, send, DM, threaded conversations, interactive approval, file ingress, file egress, proactive send, user lookup, destination lookup, and runtime health.
- [ ] 2.3 Add a standard runtime snapshot model with enabled, health, connected, ready, principal identity, endpoint identity, and activity metadata.
- [ ] 2.4 Add a registry service that enumerates descriptor and snapshot providers.

## 3. Existing adapter coverage

- [ ] 3.1 Register descriptors for Slack, Discord, Mattermost, TUI, Headless, SignalR, Reminder, and Webhook or explicitly mark unsupported/not-configured adapters.
- [ ] 3.2 Adapt Slack runtime health to the standard snapshot shape without changing Slack behavior.
- [ ] 3.3 Adapt Discord runtime health to the standard snapshot shape without changing Discord behavior.
- [ ] 3.4 Adapt Mattermost runtime health to the standard snapshot shape without actorizing it yet.
- [ ] 3.5 Represent SignalR as a daemon endpoint and TUI as a logical local client channel.

## 4. Descriptor-driven observability

- [ ] 4.1 Change daemon runtime status to enumerate the channel registry instead of hard-coding individual adapters.
- [ ] 4.2 Change daemon stats channel activity to enumerate descriptor-backed channels.
- [ ] 4.3 Preserve current status/stats output fields or provide explicit compatibility mapping.

## 5. Address resolution

- [ ] 5.1 Add a standard address resolver contract for users and destinations.
- [ ] 5.2 Support exact stable ID resolution before name search.
- [ ] 5.3 Fail loudly with candidates for ambiguous display-name matches.
- [ ] 5.4 Route resolution requests to the resolver registered for the selected descriptor or channel type.
- [ ] 5.5 Wire Slack lookup to its descriptor-scoped resolver.
- [ ] 5.6 Wire Discord lookup to its descriptor-scoped resolver where supported.
- [ ] 5.7 Wire Mattermost lookup to its descriptor-scoped resolver.

## 6. LLM-facing tool standardization

- [ ] 6.1 Define standard tool intent schemas and final tool names for send message, lookup user, and lookup destination.
- [ ] 6.2 Rename/map existing Slack tools to the standard tool names and intent schema.
- [ ] 6.3 Rename/map existing Discord tools to the standard tool names and intent schema.
- [ ] 6.4 Rename/map existing Mattermost tools to the standard tool names and intent schema.
- [ ] 6.5 Update system skills, CLI/help text, and eval cases for renamed LLM-facing channel tools.

## 7. Stateful transport lifecycle follow-up

- [ ] 7.1 Add contract tests for not-ready ingress gating, runtime disconnect health, clean reconnect signaling, and handler de-duplication for stateful remote chat adapters.
- [ ] 7.2 Implement Mattermost lifecycle actorization only after the standard snapshot and lifecycle contract tests exist.
- [ ] 7.3 Verify Slack and Discord satisfy the same lifecycle requirements or document explicit capability differences.

## 8. Validation and quality gates

- [ ] 8.1 `dotnet test src/Netclaw.Actors.Tests/ --filter Channel`
- [ ] 8.2 `dotnet test src/Netclaw.Daemon.Tests/`
- [ ] 8.3 `dotnet slopwatch analyze`
- [ ] 8.4 `./scripts/Add-FileHeaders.ps1 -Verify`
