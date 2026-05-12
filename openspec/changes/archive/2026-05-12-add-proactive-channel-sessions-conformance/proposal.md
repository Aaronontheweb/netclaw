## Why

Agent-initiated channel posts (today: `send_slack_message`; in flight: a
Discord equivalent per [issue #953][i953]) create a brand-new channel
session whose transcript is born empty. When the user replies — even
hours later in the same thread — the loaded LLM context contains none
of the message that opened the conversation, so the agent answers the
reply with no record of what it said. Concrete production repro in
Slack session `D0AC6CKBK5K/1778533824.662179`: a `DeliveryKind.None`
reminder fired, the agent DM'd the user about a (hallucinated) PR
status, the user replied two hours later, and the agent's reply was
off-topic and confabulated.

The `thread-history-backfill` capability already hydrates prior thread
messages into adopted context when an authorized inbound creates an
executable turn — that machinery is in production and correct. The
gap is that each channel's history fetcher previously *unconditionally*
filtered out bot-authored messages from the server-side history API.
For Slack: `SlackThreadHistoryFetcher.cs` dropped any message with a
`bot_id`. For Discord: `DiscordThreadHistoryFetcher` dropped any
message whose `Author.IsBot` was true at three sites. That blanket
filter dropped the one load-bearing entry for the proactive-bootstrap
case: the bot's posted message that opened the thread.

A first attempt (PR #954) removed the bot-message filter entirely and
relied on the cursor watermark to dedup. The watermark filters by
ordering key (time), not by author, and can lag advancement under
recovery — so it cannot guarantee that bot replies from below the
thread root are excluded. Result: the agent's own prior in-session
turns surfaced as third-party adopted context (issue #955).

The corrected design narrows the inclusion criterion: bot-authored
entries are hydrated **only at the thread root**. The root is the one
position whose content cannot already exist in any session's
persisted transcript (by definition no session ran in this thread
before the root was posted). Every other bot-authored entry was
produced by some session of ours and is already in transcript;
re-adopting it from server-side history would surface our own outputs
as third-party context.

[i953]: https://github.com/netclaw-dev/netclaw/issues/953

## What Changes

- Modify `thread-history-backfill` to add a cross-channel requirement
  that bot-authored history entries are hydrated **only at the thread
  root**. Entries below the root are dropped during history fetch.
  The cursor watermark is documented as a cost optimization, not the
  dedup primitive.
- Slack adapter: bot-authored entries (`bot_id` present) included
  only when `message.Ts == threadTs`. Sender id derivation: user id
  preferred over bot id; entries with neither are dropped.
- Discord adapter: bot-authored entries included only when
  `MessageId == threadChannelId` (Discord convention: thread channel
  id equals root message id).
- Inbound bot-message filters on both channels (live Events API echo
  loop-prevention) remain unchanged.
- Document the Discord DM limitation: DMs are flat (no distinct
  thread root), so this rule is inert there. The proactive-post
  amnesia scenario is not closed on Discord DMs — known limitation
  of the platform model.

Not a breaking change. Slack channel ACL gating runs ahead of the
fetcher; the newly-included entries are subject to the same
adopted-context security model that handles third-party speakers
today.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `thread-history-backfill`: adds a requirement that bot-authored
  messages from server-side history are hydrated only at the thread
  root. Existing requirements in this capability are unchanged.

## Impact

### Code

- `src/Netclaw.Channels.Slack/SlackThreadHistoryFetcher.cs` —
  bot-authored entries included only when `message.Ts == threadTs`.
- `src/Netclaw.Channels.Discord/Transport/DiscordThreadHistoryFetcher.cs` —
  bot-authored entries included only when
  `MessageId == threadChannelId`. The earlier blanket removal of three
  `IsBot` filter sites is now replaced with a single root-only
  predicate at the outer loop in `FetchThreadHistoryAsync`.
- `src/Netclaw.Actors.Tests/Channels/SlackThreadHistoryFetcherTests.cs` —
  unit-level coverage: root inclusion, below-root exclusion, mixed
  root+below-root, bot-id-only sender, user-id preference.
- `src/Netclaw.Actors.Tests/Channels/DiscordThreadHistoryFetcherTests.cs` —
  parallel Discord unit tests.
- `src/Netclaw.Actors.Tests/Channels/SlackThreadBackfillIntegrationTests.cs` —
  end-to-end regression: a session with mid-thread bot replies in
  history does NOT re-adopt them into the merged user turn; a
  proactively-posted bot root IS hydrated.

### APIs

- Internal only. No public surface change.

### Cross-channel implications

- Slack and Discord fetchers both apply the root-only rule.
- TUI / SignalR / webhook-side channels have no server-side thread
  history backfill model, so this requirement is inert for them today.
- **Discord DMs**: flat conversation, no distinct thread root. The
  rule is inert; proactive-post amnesia is not addressed on Discord
  DMs. Documented as a known platform limitation in the spec delta.

### PRD lineage

- **PRD-008** (Scheduling and Periodic Tasks) — outcomes (3) assume
  user replies to posted task results reach a coherent session. This
  change closes the conformance gap that prevented that.
- **PRD-009** (Input Adapters and Unified Input) — the unified-input
  premise that "everything is just a message arriving at a session
  actor" is upheld for the proactive bootstrap case via adopted
  context.

### Security and operational impact

- **No new attack surface.** Hydrated entries come from a thread the
  session already has channel-level ACL permission to read.
- **No privilege change.** Bot messages enter adopted context as
  quoted prior speakers — non-executable; only the current authorized
  message is executable.
- **No new persistence shape.** Uses existing adopted-context
  hydration; no new event types, no SessionState changes, no proto
  changes.
- **Existing log line covers it**: `Fetched {Count} thread history
  messages for {ChannelId}/{ThreadTs}` — count reflects the
  root-only behavior naturally.

### MVP scope statement

**In scope:**
- `thread-history-backfill` spec delta with the root-only rule.
- Slack and Discord fetcher implementations.
- Unit + integration regression tests covering the failure mode in
  issue #955.

**Out of scope:**
- `send_discord_message` proactive-post tool (issue #953).
- Discord DM amnesia (platform limitation; no thread root concept).
- TUI / SignalR / webhook proactive-post tooling.
- **Agent reasoning lineage / explainability** ("why did the agent say
  this?"). The fix makes the new session see *what* it posted, not
  *why*.
