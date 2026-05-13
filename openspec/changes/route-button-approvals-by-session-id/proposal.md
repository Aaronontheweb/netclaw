## Why

Approval button clicks in Slack and Discord are silently dropped whenever the
channel-adapter actor tree is cold at the moment the user clicks. The proximate
cause is **cascading passivation**: the per-thread binding correctly defers its
own idle timer when an approval is pending, but its channel-level parent
passivates independently and takes the child with it. When the user later
clicks the button, the re-spawned channel-level gateway has no in-memory record
of the pending approval and drops the response with `Ignoring Slack approval
response for missing thread`. The session actor remains alive and addressable
the entire time but has no way to receive the click. This is a sibling problem
to GitHub issue #939 (which covers full daemon restart via persistence) and
fires far more often, because channel-level idle passivation happens routinely
on a 2-hour timer.

## What Changes

- Move inbound approval **button** routing off the per-thread binding actor's
  in-memory `_pendingApprovalRequests` lookup and onto the deterministic
  session actor path (`session-manager/{persistenceId}`). Routing identifiers
  come from the platform payload (`channel.id`, `message.thread_ts`) and the
  existing button-value codec (`callId`, `optionKey`, `requesterSenderId`),
  which together fully resolve the destination without consulting any
  channel-adapter-internal state.
- Apply the same change to Discord (`DiscordConversationActor` and
  `DiscordSessionBindingActor`) — symmetric architecture, same bug, same fix.
- Add a new self-contained protocol message (`ApprovalResponseReceived`) that
  carries everything the session actor needs to resolve the pending tool call
  and trigger the resolved-state UI redraw.
- Session actor emits a self-contained `RenderResolvedApproval` command back
  to the slack/discord output binding for the message rewrite. The binding can
  be lazily spawned for the redraw because `BuildResolvedApprovalBlocks(...)`
  is pure — no prior in-memory state required.
- Per-thread binding becomes a thin transport. Its
  `_pendingApprovalRequests` field is removed entirely. Passivation
  deferral logic that depended on it is removed. The binding can passivate
  and re-spawn freely; nothing about correctness depends on it being hot.
- **Text** approval reply routing (Slack `A`/`B`/`C`/`D` and Discord
  equivalents) ALSO moves to blind-write. Inbound text from the platform is
  forwarded to the session actor unconditionally as today's
  `SendUserMessage` (or equivalent). The session actor classifies at the
  top of its handler: if there is a pending approval AND the text matches
  an approval option, treat as a `ToolInteractionResponse`; otherwise treat
  as a normal user message. Today's binding-side classification has the
  same cold-actor bug as buttons (re-spawned binding has empty
  `_pendingApprovalRequests` and silently misclassifies the reply); moving
  it to the session fixes both classes simultaneously.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: New requirement that approval responses (both
  button-click and text-reply forms) SHALL be routable independently of
  channel-adapter actor liveness. Delivery and approval-vs-message
  classification are the session actor's responsibility, not the binding
  actor's. Button decision semantics and approval-entry persistence are
  unchanged.
- `netclaw-slack-socket`: MODIFIED text-reply requirement so routing no
  longer depends on the thread binding actor's pending-state. The Slack
  ingress forwards inbound text unconditionally to the session actor;
  the session actor classifies. ADDED parallel button-routing requirement
  for `block_actions` payloads.
- `netclaw-discord-socket`: Symmetric — text-fallback path moves to
  session-side classification; button-click path routes via session
  regardless of `DiscordSessionBindingActor` liveness.

## Impact

**Affected code:**

- `src/Netclaw.Channels.Slack/SlackConversationActor.cs` — replace per-thread
  child lookup with `Tell` to `session-manager/{persistenceId}`
- `src/Netclaw.Channels.Slack/SlackThreadBindingActor.cs` — remove the
  `_pendingApprovalRequests` field entirely; remove approval-pending
  passivation deferral; binding becomes a pure transport (decode platform
  payload, forward to session)
- `src/Netclaw.Channels.Discord/DiscordConversationActor.cs` — symmetric
- `src/Netclaw.Channels.Discord/DiscordSessionBindingActor.cs` — symmetric;
  remove `_pendingApprovalRequests`
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` — handle new
  `ApprovalResponseReceived` protocol message; emit `RenderResolvedApproval`
- `src/Netclaw.Actors/Protocol/*` — new self-contained message types

**APIs and protocol:**

- New internal actor messages: `ApprovalResponseReceived`,
  `RenderResolvedApproval`. No external API surface changes.

**Persistence:**

- No new persisted events. This change is in-memory routing only. (The
  separate persistence work for surviving full daemon restart is tracked in
  GitHub issue #939.)

**Security:**

- No change to ACL or `CanApprove` semantics. The same authorization check
  runs in the same place; only the actor that runs it moves from the
  per-thread binding to the session actor.
- The button value codec (`ApprovalButtonValueCodec`) is unchanged. The
  100-character `MaxEncodedLength` (constrained by Discord's `custom_id` cap)
  stays as-is, and prefix-matching of truncated `callId` against the pending
  call set continues to be the decode contract — now scoped to a single
  session, where match ambiguity is trivially impossible.

**Operational:**

- Resolves a class of incidents where sessions become permanently wedged on a
  pending approval after the channel-level gateway passivates (every 2h of
  channel idle).
- No configuration, schema, or migration changes.
- No CLI surface changes.

**Observability:**

- The `Ignoring Slack approval response for missing thread` log line should
  no longer appear under normal operation. If it appears after this change,
  it indicates a genuine routing failure (e.g., the session actor is gone
  too) rather than a recoverable cold-actor case.

**Out of scope:**

- Full-restart persistence of pending approvals. Tracked separately in
  upstream issue #939.
- Changes to text-reply routing, button labels, button counts, or approval
  decision semantics.
- Any change to `tool-approvals.json` or persisted approval entries.

**Source:**

- GitHub issue: netclaw-dev/netclaw#979
- Related: netclaw-dev/netclaw#939 (sibling, complementary)
