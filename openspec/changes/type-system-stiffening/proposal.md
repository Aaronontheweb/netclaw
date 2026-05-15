## Why

PR #993 fixed a production bug where a Personal-audience Slack DM was silently
downgraded to Public, denying the operator's `shell_execute`. The root cause was
not logic — it was type shape: `ChannelInput.Audience` is `TrustAudience?`
(nullable, optional) and `MessageSourceFactory.Create` invents a default via
`input.Audience ?? options.DefaultAudience`. The compiler could not tell a
forgetful adapter from a deliberate caller. The constitution's "No silent
fallbacks" rule names this anti-pattern, but trust-bearing records across the
codebase still carry security-relevant fields as nullable-with-fallback or
sentinel-default rather than `required`. The audience field bit us first; it is
unlikely to be the last. This change makes the type system a primary correctness
gate so the next PR #993 cannot compile.

## What Changes

- **BREAKING** (internal API) — `ChannelInput`'s trust fields (`Audience`,
  `Boundary`, `Principal`, `Provenance`) become `required` and non-nullable.
  Every inbound channel adapter must supply explicit trust context.
- **BREAKING** (internal API) — `MessageSource`'s four trust fields become
  `required`; the permissive sentinel-default initializers
  (`= TrustAudience.Public`, `= SourceProvenance.StrictDefault()`, etc.) are
  removed.
- `SourceProvenance` converts to a 2-parameter primary constructor
  (`TransportAuthenticity`, `PayloadTaint` required; `SourceScope`/`SourceKind`
  remain optional `init` metadata). The `Unknown`/`Unknown` sentinel defaults
  are removed.
- The four `?? options.DefaultX` fallback arms in `MessageSourceFactory.Create`
  are deleted (unreachable once `ChannelInput` is required).
- **BREAKING** (internal API) — `SessionPipelineOptions.DefaultAudience`,
  `DefaultBoundary`, `DefaultPrincipal`, `DefaultProvenance` are removed. They
  exist only to feed the deleted fallback arms.
- Elevated-fallback escalation sites
  (`source?.Audience ?? TrustAudience.Personal` in `SessionToolExecutionPipeline`,
  `msg.Audience ?? TrustAudience.Personal.ToWireValue()` in `SubAgentActor`)
  are replaced with explicit `throw` — a missing turn source is a programming
  error, not a runtime condition.
- `NullPromptInjectionDetector` substitution via `?? new NullPromptInjectionDetector()`
  is replaced with `throw`; the null detector silently disables injection
  scanning and must never be selected by accident.
- `ToolExecutionContext.Audience` changes from wire-string `string?` to parsed
  `TrustAudience?`, so an unparseable value fails at construction rather than
  silently degrading to `Public` at gate-check time. `RunSubAgent.Audience`
  changes correspondingly.
- Persisted records (`BackgroundJobDefinition`, `ActiveJobInfo`,
  `ReminderDefinition`) make their trust fields `required` — enforcing every
  in-process construction at compile time. Backward compatibility for legacy
  JSON documents is handled at deserialization: the job/reminder stores detect
  a document missing trust fields, log a warning naming the file, and backfill
  a conservative fail-closed value (`Public` / public boundary — never the old
  `Personal`). No on-disk migration and no doctor tooling — legacy documents
  load gracefully and fail closed.

## Capabilities

### New Capabilities

- `trust-context-integrity`: Establishes the cross-cutting invariant that
  trust-bearing context (audience, principal, boundary, provenance, transport
  authenticity, payload taint) is mandatory and non-optional at every actor
  boundary, that no security-relevant field may carry a permissive or elevated
  sentinel default, and that missing trust context fails loud rather than
  silently defaulting.

### Modified Capabilities

- `netclaw-input-adapters`: Inbound channel adapters SHALL supply complete,
  explicit trust context on every `ChannelInput`; the pipeline SHALL NOT
  synthesize a default audience/principal/provenance/boundary.
- `audience-context-filtering`: The session pipeline SHALL derive audience only
  from an explicitly-supplied turn source; there is no `DefaultAudience`
  fallback.
- `background-job-execution`: Background-job submission SHALL fail loud when no
  turn source is present rather than defaulting to `Personal` audience;
  persisted job records SHALL carry explicit trust fields.
- `reminder-execution-history`: Persisted reminder definitions SHALL carry
  explicit, required trust fields; legacy documents missing them SHALL be
  backfilled at load with a conservative fail-closed value and a logged
  warning.
- `netclaw-tools`: `ToolExecutionContext` SHALL carry audience as a parsed
  `TrustAudience`, not a wire string; an unparseable audience SHALL fail at
  construction.
- `netclaw-subagents`: Sub-agent spawn messages SHALL carry an explicit parsed
  audience; a missing audience SHALL fail loud rather than defaulting to
  `Personal`.

## Impact

- **Affected code**: `Netclaw.Actors` (`Channels/`, `Sessions/Pipelines/`,
  `SubAgents/`, `Jobs/`, `Reminders/`), `Netclaw.Tools.Abstractions`
  (`ToolExecutionContext`), `Netclaw.Security` (`SecurityPolicyDefaults` —
  `ParseAudienceOrPublic` / `ResolveAudienceWithFallback` become dead code),
  `Netclaw.Channels.Slack` / `Netclaw.Channels.Discord` (binding actors and
  history fetchers), `Netclaw.Daemon` (`SignalRSessionActor`,
  `WebhookExecutionActor`), `Netclaw.Cli` (new `doctor` check).
- **APIs**: Internal-only. No wire-format or on-disk-format change — the JSON
  converter preserves the existing serialized shape. No public NuGet surface.
- **Persistence**: No on-disk or on-wire format change. Legacy
  `BackgroundJobDefinition` / `ReminderDefinition` JSON documents that predate
  this change and lack trust fields are backfilled at load with a conservative
  fail-closed value and a logged warning. `ActiveJobInfo` is protobuf-serialized
  — proto3 defaults a missing audience to `Public` (fail-closed) — so it needs
  no special handling.
- **Tests**: `Netclaw.Actors.Tests`, `Netclaw.Channels.Slack.Tests`,
  `Netclaw.Channels.Discord.Tests`, `Netclaw.Daemon.Tests` adapt mechanically
  to the required-property and primary-constructor shapes.
- **Out of scope**: The broader value-object adoption pass (Pass 7 in the
  planning doc — wrapping raw-string identifiers in value objects) is tracked
  separately and not part of this change. This change is the trust-tier
  hardening only (Passes 1–4).
