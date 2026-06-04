# Design

## Decision

Standardize the channel seam through descriptors, runtime snapshots, address
resolution, and tool intent schemas. Do not start by creating a shared channel
base actor or rewriting adapter internals.

Adapters keep their platform-specific implementation details. The daemon and
LLM-facing tool registry consume a standard description of what each adapter can
do, how healthy it is, and how names resolve to platform addresses.

Mattermost lifecycle actorization remains a valid reliability fix, but it is no
longer the top-level change. It becomes one stateful-adapter task that should be
implemented after Mattermost can report the same descriptor and runtime snapshot
shape as Slack, Discord, and future remote chat adapters.

## Transport And Channel Taxonomy

Netclaw needs to distinguish logical conversation sources from process/network
transport endpoints.

| Kind | Examples | Descriptor Meaning |
|------|----------|--------------------|
| Remote chat channel | Slack, Discord, Mattermost | External workspace/server adapter that can receive messages, send replies, resolve users/destinations, and report remote socket or API health. |
| Local client channel | TUI, future Web UI | Logical conversation source created by a local client over the daemon API. The session is channel-like, but the underlying network endpoint is SignalR. |
| Daemon endpoint | SignalR hub | Server transport endpoint used by local clients. It has endpoint health and connected-client state, but it is not itself a user-facing workspace/channel. |
| Internal source | Reminder, scheduler | Daemon-owned source that creates session turns without an external chat workspace. |
| HTTP ingress source | Webhook | External HTTP event source with routing policy, not necessarily a conversational channel. |
| Non-interactive client | Headless | One-shot or request/response source with no ongoing chat surface. |

This split lets SignalR be treated like a first-class operational endpoint
without forcing it to pretend to be Slack-like. TUI sessions still get a logical
channel descriptor because they participate in session routing and approval
capabilities.

## Component Diagram

```mermaid
flowchart TD
    Adapters[Channel adapters and endpoints] --> DP[Descriptor providers]
    Adapters --> RS[Runtime snapshot providers]
    Adapters --> AR[Address resolvers]
    DP --> CR[Channel registry]
    RS --> CR
    AR --> CR
    CR --> Status[Daemon runtime status]
    CR --> Stats[Daemon stats]
    CR --> Tools[LLM tool registry]
    Tools --> Intents[Standard tool intents]
    Adapters --> Pipeline[SessionPipeline]
    Pipeline --> Session[LlmSessionActor]
```

## Standard Descriptor Shape

Each adapter or endpoint reports a stable descriptor with these concepts:

- Stable key, channel type, display name, and kind.
- Whether it is enabled by configuration.
- Capabilities: receive messages, send messages, direct messages, threaded
  conversations, interactive approvals, file ingress, file egress, user lookup,
  destination lookup, proactive messages, and runtime health.
- Tool intents it supports, such as send message, lookup user, and lookup
  destination.
- Address namespaces it can resolve, such as user, channel, room, thread, DM,
  session, webhook source, or schedule target.

The descriptor describes what the adapter promises. It must not grant ACL
permissions by itself. Actual turn authorization continues to flow through
`ChannelInput` trust context and existing policy checks.

## Runtime Snapshot Shape

Each adapter or endpoint reports a runtime snapshot with these concepts:

- Descriptor key and channel type.
- Enabled state.
- Health status and detail.
- Connected state when meaningful.
- Ready state when meaningful.
- Bot or service principal identity when the adapter has one.
- Endpoint identity when the item is a daemon transport endpoint.
- Last known activity counters or timestamps when available.

Ready is adapter-specific but comparable. For a remote socket adapter, ready
means it can accept inbound events and send replies. For a local client channel,
ready means the session endpoint can route messages. For an internal source,
ready means its scheduler or trigger is registered.

## Address Resolution

Address resolution is standardized as an intent, not as one platform's ID model.
Resolvers accept a query and an address kind. They can return exact matches,
candidate matches, or a failure.

Rules:

- Stable IDs are accepted when supplied.
- User-facing names are searchable where the backing platform supports it.
- Ambiguous names fail loudly with candidates instead of choosing the first
  match.
- Resolvers do not silently fall back from one namespace to another.
- Resolved addresses carry both display data and stable platform IDs.

## LLM-Facing Tool Intents

The tool registry should describe channel tools in terms of standard intents:

- `send_message`: destination, text, optional thread/root target, optional
  audience/context hints.
- `lookup_user`: query, optional channel key, optional exact-only flag.
- `lookup_destination`: query, destination kind, optional channel key,
  optional exact-only flag.

The implementation can keep existing tool names such as `send_slack_message`,
`send_discord_message`, and `send_mattermost_message` during migration, but each
tool must map to the standard intent schema. A generic multi-channel tool can be
introduced after the registry can enumerate descriptors and resolvers reliably.

## Stateful Transport Lifecycle

Stateful remote chat adapters must expose lifecycle through the standard runtime
snapshot. They may implement that lifecycle with actors, hosted services, SDK
callbacks, or another serialized owner, but the observable behavior must be the
same:

- Health reports disconnected, connecting, ready, degraded, and not-ready states
  consistently.
- Ingress is gated while the adapter is not ready.
- Reconnects do not duplicate SDK event handlers.
- Unexpected disconnects can request a clean reconnect when the platform SDK
  requires a full stop/start cycle.

Mattermost likely needs an actor-owned lifecycle implementation to satisfy these
requirements. That should be implemented after the standardized snapshot shape
exists.

## Migration Plan

1. Add descriptor, runtime snapshot, address-resolution, and tool-intent
   contracts without changing adapter behavior.
2. Add contract tests that enumerate every `ChannelType` and require either a
   logical channel descriptor or an explicit endpoint/internal-source descriptor.
3. Adapt existing Slack, Discord, Mattermost, TUI, Headless, SignalR, Reminder,
   and Webhook surfaces to report descriptors and snapshots using their current
   behavior.
4. Change daemon runtime status and stats to consume the registry instead of
   hard-coded Slack/Discord lists.
5. Normalize Slack, Discord, and Mattermost send/lookup tools onto standard
   intent schemas while preserving existing tool names as aliases.
6. Add name-searchable user and destination resolvers for supported platforms.
7. Only after descriptors and snapshots are stable, implement adapter-specific
   lifecycle fixes such as Mattermost actorization.

## Non-Goals

- Do not rewrite all adapters in one pass.
- Do not create a generic channel base actor in this change.
- Do not remove existing channel-specific tool names during the first migration.
- Do not change session identity formats.
- Do not weaken ACL, audience, principal, boundary, or provenance requirements.
- Do not make SignalR pretend to be a remote chat workspace.

## Risks / Trade-offs

- A descriptor model can become too abstract. Keep it tied to current runtime
  consumers: status, stats, tools, health, and address resolution.
- Generic tools can hide platform-specific constraints. Preserve platform
  capability flags and fail loudly when a requested intent is unsupported.
- SignalR needs special handling because it is both the daemon API endpoint and
  the transport used by local logical channels. Treat endpoint health and logical
  channel capability as separate records.
- Mattermost lifecycle remains a reliability risk until actorized, but delaying
  it avoids changing adapter internals before the shared seam is defined.
