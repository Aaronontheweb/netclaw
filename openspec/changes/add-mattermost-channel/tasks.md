## 1. OpenSpec planning artifacts and traceability

- [ ] 1.1 Confirm proposal, design, and spec deltas cover gateway lifecycle, ingress/ACL, session identity, thread-history backfill, proactive sends, reminders/scheduled DM delivery, reminder-spawned sessions, and interactive approvals.
- [ ] 1.2 Verify traceability references to `PRD-009`, `PRD-001`, `PRD-002`, `PRD-008`, and `PRD-003` across change artifacts.
- [ ] 1.3 Run `openspec validate add-mattermost-channel --type change` and resolve all issues.

## 2. Project scaffolding and dependencies

- [ ] 2.1 Create `src/Netclaw.Channels.Mattermost` project; add to `Netclaw.slnx`.
- [ ] 2.2 Create `src/Netclaw.Channels.Mattermost.IntegrationTests` project; add to `Netclaw.slnx`.
- [ ] 2.3 Add the Mattermost.NET client library and Testcontainers to `Directory.Packages.props` (verify latest maintained versions); reference them from the new projects.
- [ ] 2.4 Add `ChannelType.Mattermost` to `src/Netclaw.Actors/Channels/ChannelType.cs` including `ToWireValue`, `TryFromWireValue`, and `SupportsInteractiveApproval`.
- [ ] 2.5 Add Mattermost actor-registry keys to `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs`.
- [ ] 2.6 Run `./scripts/Add-FileHeaders.ps1` so every new `.cs` file carries the copyright header.

## 3. Transport layer

- [ ] 3.1 Add `Transport/` Mattermost WebSocket gateway client wrapping Mattermost.NET event subscription; salvage and re-audit the client design from PR #877.
- [ ] 3.2 Add Mattermost REST reply client and outbound client for posting messages, resolving file details, and looking up users.
- [ ] 3.3 Add `MattermostConnectFailureClassifier` splitting failures into Fatal vs Transient.
- [ ] 3.4 Add channel constants: 16,383-char message limit with newline-aware chunking, `@username` mention stripping, file-detail resolution.

## 4. Channel lifecycle and connection-failure containment

- [ ] 4.1 Implement `MattermostChannel : IChannel, IHostedService` owning the WebSocket lifecycle and bounded-backoff reconnect loop.
- [ ] 4.2 Defer token validation to `StartAsync`; never throw from DI registration (bug-fix: connection containment `07cdbb22`/`97c4e9a6`/`e222be52`).
- [ ] 4.3 On Fatal close codes, stop the WebSocket client to prevent reconnect spam; report degraded/disconnected health via `GetHealthAsync`.
- [ ] 4.4 Ensure a missing/invalid token degrades only the Mattermost channel; the daemon and other channels keep running.

## 5. Actor hierarchy

- [ ] 5.1 Implement `MattermostGatewayActor` with an LRU dedup of processed post IDs and gateway-level ACL enforcement; drop the channel's own bot posts.
- [ ] 5.2 Implement `MattermostConversationActor` for per-channel routing, spawning bindings per thread.
- [ ] 5.3 Implement `MattermostThreadBindingActor` as a persistent, session-scoped, per-thread actor that constructs `ChannelInput` and enqueues to the session pipeline.
- [ ] 5.4 Keep pending-approval state on the session actor, not the binding; lazy-spawn passivated children (bug-fix: approval routing `00034827`).

## 6. ACL and routing policies

- [ ] 6.1 Implement `MattermostAclPolicy` mirroring `SlackAclPolicy`: channel/user allow-lists, DM handling, audience resolution via `ChannelAudiences` (including the `dm` key).
- [ ] 6.2 Implement `MattermostRoutingPolicy` for mention-only and DM mention rules.
- [ ] 6.3 Implement `MattermostAttachmentUrlTrust` with subdomain validation for attachment URLs.

## 7. Ingress normalization and session identity

- [ ] 7.1 Normalize Mattermost post events into `ChannelInput` with complete explicit trust context (audience, principal, boundary, provenance) — no pipeline-synthesized defaults.
- [ ] 7.2 Derive deterministic entity keys `{channelId}/{rootPostId}` (root post uses its own ID; DM uses the DM channel ID).
- [ ] 7.3 Deliver assistant replies into the originating Mattermost thread.
- [ ] 7.4 Use value objects (`ModelId`, `TurnNumber`, identifiers) with no implicit primitive conversions (bug-fix: value-object integrity `2d458ede`).

## 8. Thread-history backfill

- [ ] 8.1 Implement `MattermostThreadHistoryFetcher : IThreadHistoryFetcher`.
- [ ] 8.2 Hydrate bot-authored messages root-only; exclude all bot messages below the thread root (bug-fix: bot dedup `786b5985`/`45f4c57b`).
- [ ] 8.3 Use the watermark cursor only as a cost optimization, not the dedup primitive.
- [ ] 8.4 Re-arm deferred one-shot hydration so it completes on the first authorized inbound (bug-fix: deferred hydration `d806f81f`).
- [ ] 8.5 Propagate the resolved trust audience onto history-fetched `ChannelInput`s (bug-fix: audience propagation `95edfc7b`).

## 9. Proactive sends and tools

- [ ] 9.1 Implement the `send_mattermost_message` tool (`Tools/`) with channel and thread targeting.
- [ ] 9.2 Implement an acknowledged thread-initialization handshake for proactive sends (bug-fix: proactive ack `92447d90`); mark proactive threads created (bug-fix: proactive thread hardening `498428d0`).
- [ ] 9.3 Implement the `lookup_mattermost_user` tool.
- [ ] 9.4 Block proactive direct messages when DMs are disabled in channel configuration.

## 10. Reminders and scheduled delivery

- [ ] 10.1 Implement `MattermostReminderTargetResolver` canonicalizing channel and `dm:<userId>` targets; reject ambiguous bare identifiers.
- [ ] 10.2 Support `Channel`-delivery reminders that spawn a fresh continuable session keyed `schedule/{taskId}/{runTs}` with the reminder's stored audience and grants.
- [ ] 10.3 Support `CurrentSession`-delivery reminders re-entering the originating session via the gateway trusted-turn handler.
- [ ] 10.4 Prevent duplicate reminder execution with an active-execution tracker; no `ReceiveTimeout` that kills long LLM chains (bug-fix: reminder dup `b2092d19`).

## 11. Interactive approvals and callback endpoint

- [ ] 11.1 Implement `MattermostApprovalHandler` and an interactive-button approval prompt builder.
- [ ] 11.2 Implement an HMAC signer/verifier using a per-daemon ephemeral key for callback payloads.
- [ ] 11.3 Register the `/api/mattermost/actions` callback route only when the channel is enabled with interactive approvals configured.
- [ ] 11.4 Verify signature and run ACL on the resolved sender before mutating approval state; route responses by session identity into existing sessions only.
- [ ] 11.5 Implement the deterministic A/B/C/D text-reply approval fallback when interactive approvals are not configured.

## 12. Daemon wiring and configuration

- [ ] 12.1 Add `MattermostChannelOptions` with `Enabled`, bot token (`SensitiveString`), server URL, `AllowDirectMessages`, `MentionOnly`, allow-lists, `ChannelAudiences`, and interactive-approval settings.
- [ ] 12.2 Add `MattermostChannelRegistrationExtensions` registering the channel, thread-history fetcher, reminder target resolver, reply/outbound clients, tools, and event handlers.
- [ ] 12.3 Add the Mattermost section to `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` with `"default"` values so `netclaw doctor --fix` migrates pre-Mattermost configs cleanly.
- [ ] 12.4 Wire the callback route registration into the daemon HTTP host.
- [ ] 12.5 Add new persisted message types to `netclaw_messages.proto` only if required, keeping them framework-owned and serialization-safe.

## 13. Conformance contract tests

- [ ] 13.1 Add `MattermostAclContractTests` subclassing `AclPolicyContractTests`.
- [ ] 13.2 Add `MattermostGatewayContractTests` subclassing `GatewayRoutingContractTests`.
- [ ] 13.3 Add `MattermostSessionBindingContractTests` subclassing `SessionBindingContractTests`, including the thread-hydration contract.
- [ ] 13.4 Add `RecordingMattermostReplyClient` and any other required test doubles.

## 14. Unit and integration tests

- [ ] 14.1 Add unit tests for ACL policy, routing policy, message chunking, attachment URL trust, connect-failure classifier, and reminder target resolution.
- [ ] 14.2 Add unit tests for HMAC callback signing/verification and approval response routing (including the passivation-survival case).
- [ ] 14.3 Add offline tests for thread-history backfill (root-only bot dedup, deferred-hydration re-arm).
- [ ] 14.4 Add proactive-send and backfill integration tests mirroring the Slack/Discord proactive-thread suites.
- [ ] 14.5 Add Testcontainers integration tests in `Netclaw.Channels.Mattermost.IntegrationTests` against a real Mattermost server; keep them out of required CI.

## 15. Skills, evals, and docs

- [ ] 15.1 Update the `netclaw-operations` system skill for Mattermost channel config and diagnostics; bump its `metadata.version`.
- [ ] 15.2 Add an eval case for the `send_mattermost_message` tool (tool discovery/use).
- [ ] 15.3 Add a Mattermost setup and approval-callback runbook, including the HMAC ephemeral-key lifecycle.

## 16. Validation and quality gates

- [ ] 16.1 Run `openspec validate add-mattermost-channel --type change` and resolve all issues.
- [ ] 16.2 Run `dotnet build` and `dotnet test`; ensure the full suite (including the three Mattermost contract suites) passes.
- [ ] 16.3 Run `dotnet slopwatch analyze`; resolve any new violations.
- [ ] 16.4 Run `./scripts/Add-FileHeaders.ps1 -Verify`.
- [ ] 16.5 Run the eval suite for the new tool case.
- [ ] 16.6 Confirm `ConfigSchemaDoctorCheck` passes and `netclaw doctor --fix` migrates a pre-Mattermost config cleanly.
