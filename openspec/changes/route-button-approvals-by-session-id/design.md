## Context

Today, inbound approval button clicks travel through this actor topology:

```
Slack/Discord platform
        │ block_actions / interaction payload
        ▼
SlackConversationActor / DiscordConversationActor      (per-channel)
        │ Context.Child(threadName)
        ▼
SlackThreadBindingActor / DiscordSessionBindingActor   (per-thread / per-session)
        │ consults in-memory _pendingApprovalRequests
        │ matches callId
        │ builds ToolInteractionResponse
        ▼
LlmSessionActor                                        (per-session)
```

The decisive lookup happens in the per-thread binding actor: it owns
`_pendingApprovalRequests` (a `List<PendingApproval>`) and uses it to validate
the callId, run `CanApprove`, and route the response.

Outbound (LLM → user) traffic, by contrast, is sent by the session actor to
the binding's deterministic actor path. Akka.NET re-spawns whatever is needed
at that path on demand. Outbound therefore survives passivation. Inbound does
not.

The proximate incident is **cascading passivation**:

```
T+0      slack-gateway/<channelId>/<threadTs>
         "Slack thread idle but 1 approval(s) are pending; deferring passivation"
         ← per-thread child correctly defers ITS OWN timer

T+1.1s   slack-gateway/<channelId>
         "Slack conversation idle for 2 hours, passivating"
         ← CHANNEL-LEVEL parent passivates, takes the child with it
```

Five hours later, the user clicks Approve. The platform delivers the payload
correctly, but the re-spawned channel-level gateway has no in-memory record
of the pending approval and drops the response with
`Ignoring Slack approval response for missing thread`. The session actor was
alive at a deterministic path the entire time.

Constraints we must respect:

- **Akka.NET single-writer per actor.** The session actor must remain the
  sole writer of its approval state. The new path must not bypass it.
- **Discord parity.** Same actor shape, same bug, same fix required.
- **Button-value codec budget.** `ApprovalButtonValueCodec.MaxEncodedLength`
  is 100 chars (Discord `custom_id` cap), so the click payload can carry the
  encoded `callId|optionKey|requesterSenderId` triple and nothing more. We
  cannot embed the full `ToolInteractionRequest`.
- **Text reply path has the same cold-actor bug as buttons.** Today's
  binding intercepts `A`/`B`/`C`/`D`, but only because it has
  `_pendingApprovalRequests` populated. A re-spawned binding has an empty
  list and would silently misclassify the reply as a normal message. The
  bug is harder to observe because users instinctively click buttons, but
  it is the same bug class. We are fixing it together.

## Goals / Non-Goals

**Goals:**

- Inbound approval responses (button-click and text-reply alike) are
  delivered to the session actor regardless of whether any subset of the
  channel-adapter actor tree is hot at the moment of the response,
  **without requiring persistence**.
- Routing is symmetric with outbound: both addressed by `SessionId` to a
  deterministic actor path.
- Approval-vs-normal-message classification lives at the session actor
  (the only authority that knows the pending-call state). Channel
  adapters do no classification.
- Resolved-state UI redraw works without requiring prior in-memory state
  on the binding actor.
- Discord and Slack adapters get the same fix.
- `CanApprove` semantics and ACL evaluation are byte-identical to today.
- The binding actor becomes a pure transport — it can passivate and
  re-spawn freely with no impact on correctness.

**Non-Goals:**

- Surviving full daemon restart with pending approvals intact. That is
  upstream issue #939 and requires persistence work outside this change.
- Changing button labels, button counts, decision semantics, or
  `tool-approvals.json` format.
- Persisting any new state to disk in this change.
- Removing the per-thread binding actor entirely. It still owns outbound
  rendering and platform-payload decoding. It just no longer carries
  routing-relevant state.

## Decisions

### D1. Inbound routing is addressed to the session actor by SessionId, not via Context.Child

The channel-level conversation actor decodes the platform payload and the
button-value codec entirely, builds a self-contained protocol message, and
`Tell`s the session actor at its deterministic path
`session-manager/{persistenceId}`. It does not consult any per-thread child.

**Why this over the alternatives:**

- *Alternative A: Resurrect missing per-thread child.* This is what upstream
  issue #939's Phase 2 proposes. It works but requires the binding actor's
  pending-approval state to be persisted (otherwise the resurrected actor
  doesn't know the callId is pending), which couples the routing fix to
  persistence work. We want the routing fix to ship independently and on
  shorter timeline.
- *Alternative B: Add a daemon-wide pending-approval index.* A singleton
  service-style lookup. Works, but it duplicates state the session actor
  already owns and introduces a second source of truth that must be kept
  consistent. The session actor is already the single writer for its own
  approval state — leaning on that is simpler.
- *Alternative C (chosen): Route by SessionId; let the session actor be the
  authority.* The session actor is alive at a known path, already owns the
  pending-call state, already runs `CanApprove`. Inbound traffic just needs
  to find it. The SessionId is fully reconstructible from the platform
  payload (`channel.id` + `message.thread_ts`) without consulting any
  in-memory map.

### D2. Self-contained protocol messages for inbound response and outbound redraw

Two new internal actor messages:

- `ApprovalResponseReceived(sessionId, callId, optionKey, approvingSenderId,
  channel, messageTs, responseUrl)` — sent by `SlackConversationActor` /
  `DiscordConversationActor` to the session actor. Contains everything
  needed to resolve the call and to identify which platform message needs
  to be rewritten.
- `RenderResolvedApproval(channel, messageTs, request, selectedKey,
  senderId)` — sent by the session actor back to the binding when the
  redraw should fire. The binding can be cold; Akka.NET will lazy-spawn it
  for the redraw.

**Why self-contained:** receiving actors should not need to consult any
prior in-memory state to act on the message. Specifically the binding
should not need its own `_pendingApprovalRequests` populated to render the
resolved-state blocks — `BuildResolvedApprovalBlocks(request, selectedKey,
senderId)` is already a pure function.

### D3. Authorization runs at the session actor, not the binding

`CanApprove(requesterPrincipal, requesterSenderId, approvingSenderId)` is
called by the session actor on receipt of `ApprovalResponseReceived`. Today
that check runs in `SlackThreadBindingActor` / `DiscordSessionBindingActor`.
Moving it preserves the policy (and the existing implementation in
`ApprovalButtonValueCodec.CanApprove`) but co-locates it with the state
that's actually being mutated (the session's pending-call set). Net:

- One single-writer for approval state.
- One place to add audit logging if needed later.
- No reliance on the binding actor being warm to enforce authorization.

**Anti-decision:** we are NOT relaxing the check, broadening who can
approve, or changing the principal model. The decision is purely about
which actor runs an unchanged check.

### D4. CallId prefix-match contract is preserved, scoped to session

`ApprovalButtonValueCodec.MaxEncodedLength = 100` truncates the encoded
callId when needed to fit Discord's `custom_id` cap. Whoever decodes must
tolerate prefix-match against the set of pending calls in scope. Today the
gateway/binding does this match against its per-thread pending set. Under
this design, the session actor does the same match against its own
pending-call set. Match ambiguity is impossible in practice because:

- The session actor has at most a small number of in-flight calls.
- We arrive at the session actor already scoped by `(channel.id,
  message.thread_ts)`, so the comparison set is narrower than today.

Codec is unchanged.

### D5. Binding becomes a pure transport — `_pendingApprovalRequests` is removed

The field is dropped entirely. Earlier drafts of this design proposed
keeping it for "passivation deferral hint" and "text-reply matching" —
both turned out to be incoherent under the new architecture:

- **Why "passivation deferral hint" had to go.** Deferring passivation
  while approvals are pending only matters if delivery depends on the
  binding being hot. Under D1, delivery does not depend on it. Keeping
  the deferral signals "this state matters" while D1 says "this state
  doesn't matter." Both can't be true. Worse, the deferral was already
  proven non-load-bearing for the channel-level parent (see the incident
  trace in the Context section), so it offered noise rather than safety.
- **Why "text-reply matching" had to move too.** Today's binding
  intercepts `A`/`B`/`C`/`D` against `_pendingApprovalRequests`. A
  re-spawned binding has an empty list and silently misclassifies the
  reply as a normal user message. That is the same cold-actor bug as
  buttons, just rarer because users prefer clicking. Fixing buttons
  while leaving text in the same fragile shape would codify a tier-2
  path that nobody asked for.

The clean position is **blind-write end to end**:

- Binding decodes the platform payload (Slack `block_actions` or chat
  message; Discord interaction or message) into channel-agnostic
  protocol messages and forwards them to the session actor at its
  deterministic path.
- Binding holds no routing-relevant state. It can passivate at any
  time. It can re-spawn at any time. Neither affects correctness.
- Session actor is the single authority that classifies inbound text
  ("does this match a pending approval option?") and resolves
  approvals (button or text).

Outbound rendering still flows through the binding (the binding is
where the platform SDK lives), but as documented in D2 those commands
are self-contained — the binding doesn't need prior in-memory state to
render anything.

### D5a. Session-side text classification

The session actor classifies inbound text at the entry of its existing
user-message handler:

- If a pending tool call is awaiting approval AND the text matches one
  of that call's approval option keys (`A`/`B`/`C`/`D` or whatever the
  call advertised), the session resolves the approval and does NOT
  add the text to conversation history as a user turn.
- Otherwise, the text is treated as a normal `SendUserMessage` exactly
  as today.

This preserves today's user-visible behavior (an `A` typed during a
pending approval consumes the approval and isn't echoed back as a user
turn) while moving the decision to the actor that owns the pending
state.

**Why session-side classification, not a new "raw text" message
type:** the existing `SendUserMessage` path already carries everything
the session needs (text, sender, timestamp, channel context). Adding a
new wire-level message type would duplicate the path. The
classification is a single-line check at the top of the handler.

### D6. Cascading-passivation is mitigated, not fixed, by this change

We deliberately do not change the channel-level parent's passivation policy
in this change. The blind-write architecture at the routing layer makes
passivation behavior irrelevant to delivery — a stronger guarantee than
fixing the immediate deferral gap. Any actor in the channel-adapter tree
can passivate; the response still lands.

The "deferring passivation" log line and its associated logic are removed
together with `_pendingApprovalRequests` (per D5). They were noise: they
implied the deferral was load-bearing when it was not.

### D7. Conformance is documented per-channel, not generalized to a cross-channel contract — yet

The routing-independence invariant is genuinely cross-channel in spirit:
any future channel that exposes an interactive (button/widget-style)
approval surface should satisfy it. Slack and Discord already do under
this change, and a Teams or Mattermost adapter should too.

We considered promoting the invariant to a single cross-channel
`tool-approval-gates` requirement and slimming the per-channel deltas to
"this channel implements requirement X." We chose not to, for one
specific reason: **a spec requirement is only enforceable to the extent
we can test it.** Today, what we can actually verify is per-channel:

- Slack integration test: click delivered after channel-level adapter
  passivated → response reaches the session
- Discord integration test: equivalent scenario for Discord

The mechanics differ enough between Slack (`block_actions`,
`chat.update`, no edit-window cap) and Discord (interaction tokens,
15-min interaction-edit window, `custom_id` ≤ 100 chars) that the two
adapters do not share an `IInteractiveApprovalIngress` interface today.
Without a shared interface there is no shared contract test, and a
cross-channel spec requirement would be mechanically unenforceable for
any future channel — relying solely on code review against the spec.

**What we are doing instead:**

- Per-channel spec deltas accurately describe what each channel must do
  and what we can actually test.
- This decision (D7) records the conformance intent so a reviewer of
  the next channel adapter can see what invariant the precedent is
  trying to preserve.

**When to revisit:**

- A third channel is added that supports interactive approval. At that
  point the right move is to extract `IInteractiveApprovalIngress` (or
  similar), write one shared contract test, and refactor all three
  adapters to implement it. The cross-channel spec requirement can then
  land alongside the contract test and become mechanically enforceable.
- Discovery that any **existing** channel violates the invariant in a
  way per-channel tests don't catch.

**Anti-decision:** do not write a cross-channel `tool-approval-gates`
requirement that we cannot enforce without an interface that does not
yet exist.

## Risks / Trade-offs

- **[Risk] Session actor receives an `ApprovalResponseReceived` for a
  callId it no longer holds.**
  Can happen if (a) the session itself was passivated and a stale callId
  is still encoded in a button the user clicks, or (b) two clicks race.
  **Mitigation:** session actor must respond gracefully to unknown
  callId — log + ignore, never crash. This is the same behavior the
  binding has today for the same case.

- **[Risk] Lazy-spawned binding fails to redraw.**
  If the `RenderResolvedApproval` arrives at a binding that cannot reach
  the platform (e.g., Slack rate-limited, response_url expired past 30
  minutes), the message stays in its pre-resolution form. The approval
  decision is still applied to the session; only the visual state is
  stale.
  **Mitigation:** redraw is best-effort and idempotent; failures log but
  do not block the tool call. The user can also tell the resolution from
  subsequent assistant output.

- **[Risk] Authorization check moves between actors.**
  Tests that exercise `CanApprove` via the binding actor's mailbox need
  to be updated. Functional behavior is unchanged.
  **Mitigation:** add direct unit tests on the session actor's handler;
  retain existing binding-actor tests for the text-reply path where
  `CanApprove` still runs there.

- **[Risk] Prefix-match scope change.**
  Today's prefix-match runs against a per-thread set; under this design
  it runs against a per-session set. The two are usually identical (one
  session per thread), but a session that somehow holds calls from
  multiple thread origins could theoretically see ambiguity.
  **Mitigation:** in practice sessions are 1:1 with threads. Add an
  assertion / log if the session's pending set contains multiple
  prefix-matches and use the first match (today's behavior); this
  surfaces the edge case without breaking anything.

- **[Risk] Session-side text classification consumes `A` typed in
  ambiguous contexts.**
  If a user types just `A` while a pending approval is open but means
  it as a normal message, the session resolves the approval. This is
  exactly today's behavior — the binding does the same — but the
  decision now lives in the session.
  **Mitigation:** none needed; behavior parity is the goal.

- **[Risk] Removing `_pendingApprovalRequests` breaks anything that
  reads it for non-routing reasons.**
  Searching the codebase before the refactor is required. Anything
  found that reads the field needs an explicit decision: relocate to
  the session, replace with a derived signal, or remove.
  **Mitigation:** task 3.4 / 4.3 explicitly require an audit before
  field deletion.

- **[Trade-off] No persistence in this change.**
  Daemon restart still wedges sessions on pending approval (issue #939).
  We accept this because the persistence work is larger, riskier, and
  fires far less often than channel-level passivation.

## Migration Plan

This is an internal routing change with no schema or wire-format
implications. No migration needed.

**Deployment:**

- The new protocol messages live in `Netclaw.Actors.Protocol` alongside
  existing channel-mediated approval messages.
- The conversation-actor change is a swap of one `Tell` target for
  another; backwards compatibility is not relevant because both actor
  types live in the same process and roll together.
- Daemons in the field will pick up the fix on next release. Already-
  pending approvals from before the upgrade still wedge if the user
  doesn't click before the upgrade; after upgrade, all subsequent clicks
  route correctly regardless of channel-tree liveness.

**Rollback:**

- Revert the commit. No persisted state to unwind.

## Open Questions

- **Q1:** Should the session actor's handler emit a diagnostic event when
  it accepts an approval response without an active per-thread binding
  (i.e., the route would have failed under the old code)? Useful for
  observability but adds a new SessionOutput event type.
  **Tentative:** no for this change; can be added later if needed.

- **Q2:** Do we want a near-term mitigation patch (parent gateway
  consults child pending state before passivating) shipped alongside the
  architectural fix? It would close the same hole with less code, but
  would be redundant once this change lands.
  **Tentative:** skip it. Land the architectural fix and let it stand
  alone.
