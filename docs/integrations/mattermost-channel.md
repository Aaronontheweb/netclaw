# Mattermost Channel Integration

This runbook describes how to configure and operate the Netclaw Mattermost
channel.

## Overview

The Mattermost channel provides Slack/Discord-parity behavior with default-deny
ACL controls, against a self-hosted Mattermost server.

- Session identity: `{channelId}/{rootPostId}`
- Transport: Mattermost WebSocket for events, REST for replies/outbound
- Inbound handling: ACL-gated before session dispatch
- Thread history: root-only bot-message backfill with one-shot hydration
- Approvals: interactive buttons (when a callback URL is configured) with a
  deterministic text-reply fallback
- Reminder support: `current_session`, and `channel` delivery to both channels
  and direct messages

## Prerequisites

- A self-hosted Mattermost server reachable from the Netclaw daemon
- A Mattermost bot account and access token
- 26-character Mattermost IDs for channels and/or users you plan to allow

## Configuration

Put non-secret behavior in `~/.netclaw/config/netclaw.json`:

```json
{
  "Mattermost": {
    "Enabled": true,
    "ServerUrl": "https://mm.example.com",
    "CallbackUrl": "https://netclaw.example.com/api/mattermost/actions",
    "DefaultChannelId": "abcdefghijklmnopqrstuvwxyz",
    "AllowDirectMessages": false,
    "MentionOnly": true,
    "MentionRequiredInDm": false,
    "AllowedChannelIds": ["abcdefghijklmnopqrstuvwxyz"],
    "AllowedUserIds": ["zyxwvutsrqponmlkjihgfedcba"],
    "ChannelAudiences": {
      "dm": "team",
      "abcdefghijklmnopqrstuvwxyz": "team"
    }
  }
}
```

Put secrets in `~/.netclaw/config/secrets.json`:

```json
{
  "Mattermost": {
    "BotToken": "your-mattermost-bot-token"
  }
}
```

Supported Mattermost settings:

- `Enabled` - toggles Mattermost channel startup
- `ServerUrl` - base URL of the Mattermost server
- `BotToken` - bot access token (secret)
- `CallbackUrl` - URL Mattermost can reach for interactive button callbacks.
  When set, approvals render as interactive buttons; when unset, approvals use
  the text-reply fallback and no inbound HTTP endpoint is exposed.
- `DefaultChannelId` - optional primary channel allow entry
- `AllowDirectMessages` - defaults to `false` (secure by default)
- `MentionOnly` - when `true` (default), the bot only responds to messages that
  mention it in non-DM channels
- `MentionRequiredInDm` - when `true`, a mention is required even in DMs;
  defaults to `false`
- `AllowedChannelIds` - allow-list for non-DM channel traffic
- `AllowedUserIds` - optional user allow-list (empty means no user filter)
- `ChannelAudiences` - optional audience override map; keys are channel IDs or
  `dm`, values are `personal`, `team`, or `public`

## ACL policy model

Mattermost ACL evaluation is fail-closed, identical in shape to Slack:

- Missing sender ID is denied.
- DMs are denied unless `AllowDirectMessages = true`.
- Non-DM traffic is denied unless the channel is in `AllowedChannelIds` or
  matches `DefaultChannelId`.
- If `AllowedUserIds` is non-empty, the sender must be listed.
- Audience resolves from `ChannelAudiences[channelId]`, then
  `ChannelAudiences["dm"]`, then the allow-list fallback.

## Interactive approvals

Mattermost delivers interactive button clicks over an inbound HTTP POST, unlike
Slack Socket Mode or the Discord gateway. When `CallbackUrl` is configured the
daemon exposes `/api/mattermost/actions`:

- Button callbacks carry opaque one-time action tokens. Tokens are consumed once,
  expire automatically, and buttons minted by a previous daemon process are
  rejected after a restart.
- Every callback is token-validated, ACL-checked, and bound to the original
  Mattermost channel/post before any approval state changes; the endpoint never
  creates a new session.
- When `CallbackUrl` is not configured the endpoint is not registered and
  approvals fall back to deterministic A/B/C/D text replies.

## Reminder targeting

`set_reminder` supports Mattermost in two delivery patterns:

- `delivery_kind = "current_session"` for in-thread replies
- `delivery_kind = "channel"` with `delivery_transport = "mattermost"`

Mattermost reminder target resolution requires an explicit prefix, because
Mattermost user IDs and channel IDs are both 26-character alphanumeric strings
and cannot be told apart:

- `@<userId>` - delivers to that user's direct message
- `channel:<channelId>` - delivers to that channel

A bare ID with no prefix is rejected with a disambiguation error. Direct-message
delivery is supported (it is not on Discord) because a Mattermost DM is an
addressable channel.

Reminder channel delivery maps to `send_mattermost_message`.

## Runtime behavior and troubleshooting

- If Mattermost is disabled, the daemon starts normally and Mattermost stays
  inactive.
- A missing/invalid `BotToken` or `ServerUrl`, or any connection failure, is
  contained: the Mattermost channel degrades on its own and the daemon plus all
  other channels keep running. `netclaw status` shows the channel as
  `disconnected` with a reason.
- A fatal failure (bad token, unreachable server) stays offline until the
  configuration is fixed and the daemon is restarted; a transient network
  failure retries automatically on a bounded backoff.

Common failure patterns:

- `Mattermost is enabled but Mattermost:ServerUrl is not configured.`
  - Add `Mattermost.ServerUrl` to `netclaw.json`.
- `Mattermost is enabled but no bot token is configured.`
  - Add `Mattermost.BotToken` to `secrets.json`.
- `Mattermost rejected the bot token (HTTP 401).`
  - Re-issue the bot token and update `secrets.json`.

## Security notes

- Treat Mattermost bot tokens as secrets. If a token appears in logs, chat,
  shell history, or commits, rotate it immediately and update `secrets.json`.
- `/api/mattermost/actions` is the first channel-owned inbound HTTP endpoint in
  Netclaw. It is only registered when the channel is enabled with a
  `CallbackUrl`, is one-time-token-validated and ACL-checked, fails closed on
  invalid, expired, or replayed tokens, and routes only into existing sessions.
  If you do not need interactive approval buttons, leave `CallbackUrl` unset to
  keep the inbound HTTP surface closed.
