## Why

`PRD-009` defines the core input-adapter principle: all inputs should arrive at
the session pipeline through the same transport-agnostic boundary, with source
metadata and instructions carrying the channel-specific differences. The current
implementation has achieved that for `ChannelInput`, but the operational and
LLM-facing surfaces still drift by adapter.

Current gaps include:

- Runtime status and stats are partially hard-coded to specific adapters.
- Slack, Discord, and Mattermost expose different LLM-facing tool shapes.
- User and destination lookup is inconsistent and often ID-first instead of
  name-searchable.
- `ChannelType` mixes logical conversation sources with transport endpoints,
  which makes SignalR/TUI/headless harder to reason about consistently.
- Stateful socket adapters expose different lifecycle health semantics.

The Mattermost lifecycle issue is one symptom of this broader problem. The first
change should standardize the channel contract that every adapter reports to the
daemon and tools. Adapter-specific lifecycle fixes, including Mattermost
actorization, should happen after that seam exists.

Source PRDs/specs: `PRD-009-input-adapters-and-unified-input.md`,
`SPEC-011-daemon-architecture.md`, `openspec/specs/netclaw-input-adapters/spec.md`.

## What Changes

- Add a standard channel descriptor contract for all logical conversation
  sources and daemon transport endpoints.
- Add a standard runtime snapshot contract for health, readiness, enabled state,
  endpoint identity, bot identity, and capability reporting.
- Add standard address-resolution semantics so users, channels, rooms, threads,
  and destinations can be resolved by stable IDs or user-facing names.
- Add standard LLM-facing tool intent schemas for send-message and lookup tools,
  allowing current per-channel tool names to be renamed to the standardized
  surface during migration.
- Change daemon runtime status and stats to enumerate registered descriptors
  instead of hard-coding individual adapters.
- Define socket-adapter lifecycle requirements that Mattermost, Discord, Slack,
  and future remote chat adapters can satisfy without requiring a shared base
  actor.

## Capabilities

### New Capabilities

<!-- None. This extends the existing input-adapter capability. -->

### Modified Capabilities

- `netclaw-input-adapters`: Add requirements for standardized channel
  descriptors, runtime snapshots, address resolution, LLM tool intents,
  descriptor-driven observability, and reliable stateful transport lifecycle
  reporting.

## Impact

- **Affected systems:** channel abstractions, daemon runtime status, daemon
  stats, Slack/Discord/Mattermost LLM tools, channel user/destination lookup,
  channel contract tests, and stateful adapter lifecycle tests.
- **Security:** no new ACL bypass. Standardized descriptors must preserve the
  source audience, principal, boundary, and provenance already required by
  `ChannelInput`.
- **Reliability:** health and readiness become comparable across adapters.
  Stateful socket adapters expose reconnect and not-ready states through a
  common snapshot shape.
- **Compatibility:** no session identity change is required. Existing
  LLM-facing channel tool names may change as part of standardization; system
  skills and evals must be updated when tool names change.
