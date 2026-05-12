## ADDED Requirements

### Requirement: Bot-authored messages are hydrated from server-side history only at the thread root

Threaded channel adapters SHALL include a bot-authored message from
server-side thread history if and only if that message is the thread
root. A "bot-authored" message is one whose author is identified by
the platform as a bot (Slack: `bot_id` present; Discord: `Author.IsBot`).
The "thread root" is the message whose platform identifier equals the
thread's identity key (Slack: `ts == thread_ts`; Discord:
`MessageId == thread channel id`).

Bot-authored entries below the thread root SHALL be dropped during
history fetch. They are the agent's own (or another session's) prior
in-session outputs, which are already persisted in some session's
transcript via the normal output pipeline. Re-adopting them from
server-side history would surface our own outputs as third-party
adopted context, which is the failure mode this rule prevents
(see issue #955).

The root-only restriction SHALL apply to all bot identities, not only
the local agent's. Channel-level ACL filters (configured per channel)
already determine whether the destination session has permission to
see the thread at all; this requirement does not relax or replace
those filters.

The watermark mechanism defined elsewhere in this capability
("Authorized sync watermark and gap computation") SHALL remain a cost
optimization for repeat fetches; it SHALL NOT be relied upon for
bot-vs-not-bot correctness. The watermark filters by ordering key
(time), not by author, and can lag advancement under crash recovery
or out-of-order delivery — so it cannot be the primitive that
guarantees the agent's own outputs aren't re-adopted.

The fetch SHALL derive a stable sender identifier for each retained
entry. When the platform provides a user id (e.g., Slack's `user`
field on a bot post, Discord's `Author.Id`), the adapter SHALL prefer
it. When only a bot identifier is available (e.g., Slack's `bot_id`
without a `user`), the adapter SHALL use that bot identifier as the
sender id. When neither is available, the entry SHALL be dropped.

The inbound bot-message filter that channel adapters apply to live
inbound events for loop-prevention purposes (e.g., Slack's
`IsBotMessage → drop` at `SlackConversationActor.cs:50`) SHALL remain
unchanged. That filter operates on the live inbound path; this
requirement governs the server-side history-fetch path. The two paths
are independent.

This requirement is inert on channel adapters whose "threads" have no
notion of a distinct thread root — most notably Discord direct
messages, which are a flat conversation in a DM channel. In those
cases, no entry satisfies "MessageId equals thread root id," so no
bot-authored content is hydrated from history. The proactive-post
amnesia scenario is therefore not addressed on Discord DMs; this is a
known limitation of the platform model and is not in scope for this
spec.

#### Scenario: Bot's own posted message at thread root is hydrated as adopted context

- **GIVEN** a channel session that was created by an agent-initiated
  proactive post such that the bot's message is the thread root
- **AND** the producing ephemeral session has terminated and the
  destination session's transcript is empty
- **WHEN** a user replies in the thread, creating an authorized inbound
- **THEN** the history fetcher returns the bot's posted message as an
  entry with the bot's sender id
- **AND** the adopted-context merge layer includes the entry in the
  authorized turn's adopted-context window before the user reply

#### Scenario: Bot reply below the thread root is dropped from history backfill

- **GIVEN** a channel session with a thread that has at least one
  prior agent-authored reply persisted as a turn in the session
  transcript
- **AND** that prior reply also exists in the platform's server-side
  thread history at an ordering key strictly greater than the thread
  root
- **WHEN** the history fetcher hydrates the thread for a subsequent
  authorized inbound
- **THEN** the prior agent-authored reply is NOT included in the
  fetched-history result
- **AND** the adopted-context window built from the fetched history
  does NOT contain the agent's prior reply as a third-party speaker

#### Scenario: Bot-at-root coexists with bot-below-root

- **GIVEN** a proactively-posted thread whose root is bot-authored
- **AND** the thread also contains at least one subsequent
  bot-authored reply (the agent's first in-session turn after the user
  replied)
- **WHEN** the history fetcher hydrates the thread for a later
  authorized inbound
- **THEN** the root bot message IS included in the fetched-history
  result
- **AND** the subsequent bot reply is NOT included

#### Scenario: Human messages are hydrated regardless of position

- **GIVEN** a thread with a human-authored root and multiple
  human-authored replies
- **WHEN** the history fetcher hydrates the thread
- **THEN** all human-authored entries are included irrespective of
  their position relative to the root

#### Scenario: Bot id is the sender fallback when user id is missing

- **GIVEN** a server-side history entry that has a bot id but no user
  id and is at the thread root
- **WHEN** the history fetcher converts the entry to a `ChannelInput`
- **THEN** the resulting input's `SenderId` is the bot id

#### Scenario: User id is preferred over bot id when both are present

- **GIVEN** a server-side history entry that has both a user id and a
  bot id (common for Slack bot posts authored by a workspace bot user)
  and is at the thread root
- **WHEN** the history fetcher converts the entry to a `ChannelInput`
- **THEN** the resulting input's `SenderId` is the user id, not the bot id

#### Scenario: Entries with neither user id nor bot id are dropped

- **GIVEN** a server-side history entry that has neither a user id nor a
  bot id (e.g., a system message subtype with no author)
- **WHEN** the history fetcher iterates the entry
- **THEN** the entry is dropped from the hydration result

#### Scenario: Discord DM has no thread root, so no bot content hydrates from history

- **GIVEN** a Discord DM session, where the conversation is flat (no
  distinct thread root) and the session id is keyed on the DM channel
  identifier
- **WHEN** the history fetcher iterates server-side history for the DM
- **THEN** no message satisfies the thread-root predicate
- **AND** no bot-authored entry is hydrated
- **AND** the proactive-post amnesia scenario is not closed on this
  platform shape (known limitation)
