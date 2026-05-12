## Context

A reminder fires inside an ephemeral session. The session's LLM calls
`send_slack_message` (or any future channel's equivalent), which posts
a new top-level thread and asks the gateway to spawn a binding actor
for the new `{channelId}/{threadId}` session id. The binding actor
calls `EnsureInitializedAsync` and acks; the originating tool returns
success; the ephemeral session terminates. The new thread session is
alive but has no transcript content of its own — the posted message
was written only to the platform, never to any in-session output
pipeline.

When the user replies in that thread, the binding actor processes the
inbound. The existing `thread-history-backfill` machinery runs — it
fetches prior thread messages from the platform's server-side history
API and merges them into adopted context. Before this change, both
fetchers (Slack and Discord) unconditionally dropped bot-authored
entries; so the fetched-history result was empty and the LLM
responded to the user with only the user's message in context plus
whatever memory adoption surfaced. That's the amnesia/confabulation
in the production repro.

A first attempt (PR #954) removed the bot-message filter entirely
and relied on the cursor watermark to dedup. The watermark filters
by ordering key, not by author, and can lag advancement under
recovery. Result: bot replies that the agent had already produced
in-session and persisted in transcript were *re-adopted* from
server-side history as third-party speakers in the adopted-context
window. This is the regression in issue #955.

The corrected design narrows the inclusion criterion to **the thread
root only**. That's the one position whose content cannot already
exist in any session's persisted transcript (no session ran in this
thread before the root was posted). Every other bot-authored entry
was produced by some session of ours and is already in transcript.

## Goals / Non-Goals

**Goals:**

- Close the proactive-post amnesia bug (issue #954's original target)
  without re-adopting our own outputs (issue #955's regression).
- Specify the rule cross-channel so Slack and Discord both apply it,
  and any future channel with server-side history backfill inherits it.
- Carry an end-to-end regression test that would have caught issue
  #955 in pre-merge CI.

**Non-Goals:**

- Discord DMs. A DM is a flat channel with no thread-root concept; the
  rule is inert and the amnesia scenario is not closed there. Known
  platform limitation.
- Recovery of the producing ephemeral session's reasoning. The fix
  makes the new session see *what* it posted, not *why*.
- A new bootstrap protocol field on `StartProactiveThread`,
  transcript-seeding events, or output-pipeline suppression — all
  considered and rejected in favor of the history-fetcher fix.
- Explicit "assistant"-role tagging of the agent's own adopted-context
  entries. The renderer presents entries with sender id; system-prompt
  identity grounding suffices.

## Decisions

### Decision 1: Inclusion criterion is "bot-authored AND at thread root"

A history-fetched entry is hydrated into adopted context unless:

- it is bot-authored, AND
- it is not the thread root.

Concretely:
- Slack: bot-authored ≡ `bot_id` present; thread root ≡ `ts == threadTs`.
- Discord: bot-authored ≡ `Author.IsBot`; thread root ≡
  `MessageId == threadChannelId` (Discord convention: a thread's
  channel id equals its root message id).

The predicate runs in the outer fetcher loop so it applies regardless
of whether the underlying message source is the real platform API or
a test fake. Inner per-page filters could be added later as a cost
optimization but are not required for correctness.

**Alternatives considered:**

| Option | Why rejected |
|---|---|
| Trust the cursor watermark to dedup (issue #954 design) | Watermark filters by time, not author; can lag advancement; cannot distinguish "our own prior turn" from "other speaker." Surfaced as issue #955 in production within a day of merge. |
| Add `Message` payload to `StartProactiveThread`; seed transcript at bootstrap time | Requires new protocol field, new persisted event, new SessionState handling, new subscriber dispatch suppression. ~200+ LOC and four new touchpoints vs. a ~6-LOC predicate per fetcher. |
| Capture the bot-message echo via Slack Events API | Echoes are filtered as loop-prevention; flipping the filter breaks the loop guard. Even with selective filtering, dedup against the output pipeline is fiddly and pure latency loss. |
| Distinguish "our bot" from "other bots" at fetch time and only filter the former | Adds configuration coupling (we'd need to know our own bot identity at the fetcher layer) and doesn't address third-party bot replies that shouldn't surface as adopted context either. The root-only rule generalizes cleanly. |

### Decision 2: Cursor watermark is a cost optimization, not the dedup primitive

The watermark continues to filter entries by ordering key so we don't
needlessly refetch and convert. But correctness no longer depends on
it. The root-only predicate is the load-bearing primitive: even with
a stuck or lagging watermark, the predicate keeps the agent's own
outputs from being re-adopted.

### Decision 3: Sender id derivation prefers user id, falls back to bot id

For Slack, a bot post may carry both `user` (the workspace user) and
`bot_id` (the integration id). The user id matches the agent's known
identity in system-prompt grounding, so it wins. Bot id is the
fallback when only it is present. Entries with neither are dropped.

Discord exposes a single `Author.Id` for every message, so this
question doesn't arise there.

### Decision 4: Inbound bot-message filter is untouched

Both channels' inbound bot-message drop filters (Slack's
`SlackConversationActor.IsBotMessage → drop`, Discord's analog) are
loop-prevention on the live ingress path and remain unchanged. They
operate independently of server-side history fetch.

### Decision 5: Discord DM is a known limitation

A Discord DM is a flat channel; there is no distinct thread root. The
root-only predicate evaluates to "no message qualifies," so no
bot-authored content hydrates. The proactive-post amnesia scenario is
not closed for Discord DMs by this change. We accept this as a
platform limitation.

### Decision 6: Cross-channel conformance lives in `thread-history-backfill`

The new requirement is added to the existing `thread-history-backfill`
capability, not a new spec. The rule refines how hydration filters,
which is structurally part of that capability.

## Risks / Trade-offs

| Risk | Mitigation |
|---|---|
| Watermark advancement bug causes re-fetch but predicate keeps correctness | Already addressed by Decision 2; regression test asserts the predicate's correctness independently of watermark state. |
| Bot-authored thread root that the agent later wants to refute can't be distinguished from "our own bot's prior in-session turn" by sender id alone | The root entry's status as "the entry our session was bootstrapped against" is fully captured by its position; sender id only carries identity, not bootstrap-vs-not. Acceptable for MVP; we can add an explicit provenance bit later if evals show ambiguity. |
| Third-party bot replies in busy channels are excluded from adopted context | Acceptable: the existing model treats them as channel context the agent has permission to see but not as load-bearing for any one session's turn. If a deployment wants them included, a layered ACL filter could be added; out of scope. |
| Discord DM amnesia | Documented as a known limitation. Tracked separately if/when a Discord DM proactive-post pattern needs first-class support. |

## Actor boundaries and persistence implications

- **No actor topology changes.** The fix lives inside the channel-
  specific history fetchers, invoked by the existing hydration path.
- **No new persisted events.** Bot-root content enters via adopted
  context, already persisted as `AdoptedContextRecorded`.
- **No watermark changes.** Watermark semantics unchanged; it remains
  a cost optimization on top of the root-only predicate.
- **No subscriber dispatch changes.** Fix never enters the output
  pipeline.

## Failure modes and recovery behavior

| Failure | Visible effect | Recovery |
|---|---|---|
| Slack `conversations.replies` returns no entries | Fetcher returns empty list | Existing fallback; no regression. |
| Slack/Discord history API rate-limited or errors | Fetcher catches and returns empty list | Existing behavior; logged warning. |
| Watermark stuck at thread root after restart | Fetcher refetches the gap on every inbound | Cost regression only; correctness unchanged (root-only predicate still applies). Tracked separately if profiling shows impact. |
| Bot's workspace user id changes (rare) | Adopted-context entry has old sender id; LLM may not recognize as self | Identity grounding lists current + recent prior user ids; eval suite can be extended. Not addressed here. |

## Migration Plan

Forward-only. No data migration. No new persistence shape.

**Rollback strategy:** the predicate in each fetcher can be removed
to restore PR #954's "include all bot history" behavior (which would
reintroduce issue #955). Reverting both PRs in this thread restores
the original "drop all bot history" behavior (which would reintroduce
the amnesia bug). The regression tests stay in the suite either way.

**Order of merge:**

1. Spec delta + proposal/design/tasks revisions (this change).
2. Slack + Discord fetcher implementations.
3. Unit + integration regression tests.
4. Archive the OpenSpec change in the same PR to update
   `openspec/specs/thread-history-backfill/spec.md` with the corrected
   delta.

## Open Questions

1. **Cost optimization at the inner fetch layer.** Discord's
   `FetchRawMessagesAsync` currently builds `HistoricalMessage` records
   for all entries including bot-below-root; the outer predicate then
   discards them. Negligible alloc cost in practice. If profiling
   shows hot spots, restore inner-loop `IsBot` filters for non-root
   positions.

2. **Explicit self-tagging in adopted-context renderer.** If evals
   show the LLM confuses its own prior outputs with third-party
   speakers when sender id is the only signal, we add a per-entry
   "self" attribute. Defer pending eval signal.
