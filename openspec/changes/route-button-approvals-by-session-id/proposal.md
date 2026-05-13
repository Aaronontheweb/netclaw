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
- Per-thread binding's `_pendingApprovalRequests` is no longer load-bearing
  for delivery. It is retained only as a hint for the binding's own
  passivation deferral.
- **Text** approval reply routing (Slack `A`/`B`/`C`/`D` and Discord
  equivalents) is **unchanged**. Text replies legitimately need pending-state
  lookup at the binding actor because the letter alone is ambiguous without
  knowing what's pending. Buttons carry an unambiguous encoded `callId`
  in the click payload, so they don't need it.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: New requirement that **button** approval responses
  SHALL be routable independently of channel-adapter actor liveness — i.e.,
  delivery does not depend on the per-thread binding actor being hot at the
  moment the user clicks. The existing button-semantics requirements (which
  decision each click produces, ACL evaluation, persistence of approval
  entries) are unchanged.
- `netclaw-slack-socket`: Clarify that the existing "Slack text approval
  reply routing" requirement (which mandates routing through the thread
  binding actor's pending state) applies to **text** replies only, and add
  a parallel requirement covering button-click routing that does not rely
  on the binding actor's liveness.
- `netclaw-discord-socket`: Symmetric clarification — button approval routing
  does not depend on the per-channel binding actor's liveness.

## Impact

**Affected code:**

- `src/Netclaw.Channels.Slack/SlackConversationActor.cs` — replace per-thread
  child lookup with `Tell` to `session-manager/{persistenceId}`
- `src/Netclaw.Channels.Slack/SlackThreadBindingActor.cs` — remove
  `_pendingApprovalRequests` as a routing dependency for buttons (keep as
  passivation-deferral hint)
- `src/Netclaw.Channels.Discord/DiscordConversationActor.cs` — symmetric
- `src/Netclaw.Channels.Discord/DiscordSessionBindingActor.cs` — symmetric
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
