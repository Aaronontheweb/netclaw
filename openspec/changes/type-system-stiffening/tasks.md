## 1. PR-A — Trust-bearing record shapes

- [x] 1.1 Convert `SourceProvenance` (`Netclaw.Actors/Channels/SourceProvenance.cs`) to a 2-parameter primary constructor `(TransportAuthenticity, PayloadTaint)`; keep `SourceScope`/`SourceKind` as optional `init`; remove the `Unknown` sentinel defaults.
- [x] 1.2 Remove the `SourceProvenance.StrictDefault()` factory; update its callers to construct an explicit `new SourceProvenance(TransportAuthenticity.Unverified, PayloadTaint.Public)` where a conservative value is genuinely needed.
- [x] 1.3 Make `ChannelInput.Audience`, `Boundary`, `Principal`, `Provenance` (`Netclaw.Actors/Channels/ChannelInput.cs`) `required` and non-nullable.
- [x] 1.4 Make `MessageSource.Audience`, `Boundary`, `Principal`, `Provenance` (`Netclaw.Actors/Channels/MessageSource.cs`) `required`; delete the four sentinel-default initializers.
- [x] 1.5 Remove the four `?? options.DefaultX` fallback arms in `MessageSourceFactory.Create` (`Netclaw.Actors/Channels/ChannelPipeline.cs`); assign trust fields directly from `input`.
- [x] 1.6 Delete `SessionPipelineOptions.DefaultAudience`, `DefaultBoundary`, `DefaultPrincipal`, `DefaultProvenance`.
- [x] 1.7 Update `SlackThreadBindingActor.BuildOptions` and `SlackThreadHistoryFetcher.ConvertMessageAsync` to stamp explicit `Audience`, `Boundary`, `Principal`, `Provenance` onto every `ChannelInput`.
- [x] 1.8 Update `DiscordSessionBindingActor.BuildOptions` and `DiscordThreadHistoryFetcher` to stamp explicit trust context onto every `ChannelInput`.
- [x] 1.9 Update `SignalRSessionActor.BuildOptions` (`Netclaw.Daemon/Gateway`) to stamp explicit trust context.
- [x] 1.10 Update `WebhookExecutionActor.InitializeAsync` (`Netclaw.Daemon/Webhooks`) to stamp explicit trust context onto its `ChannelInput`.
- [x] 1.11 Update `ReminderExecutionActor.InitializeAsync` (`Netclaw.Actors/Reminders`) to stamp explicit trust context onto its `ChannelInput`.
- [x] 1.12 Fix all remaining compiler errors from the required-property change across `Netclaw.Actors`, channel projects, and `Netclaw.Daemon`.
- [x] 1.13 Update affected unit tests (`Netclaw.Actors.Tests`, `Netclaw.Channels.Slack.Tests`, `Netclaw.Channels.Discord.Tests`, `Netclaw.Daemon.Tests`) to construct trust-bearing records with explicit trust context.
- [x] 1.14 Verify PR-A: `dotnet build` clean, `dotnet test` green for affected projects, `dotnet slopwatch analyze` no new violations, `./scripts/Add-FileHeaders.ps1 -Verify` passes.

## 2. PR-B — Elevated-fallback sites become explicit throws

- [ ] 2.1 Replace `source?.Audience ?? TrustAudience.Personal` / `source?.Boundary ?? SecurityPolicyDefaults.PersonalBoundary` in `SessionToolExecutionPipeline` (background-job submission) with an explicit `throw new InvalidOperationException` on a missing turn source.
- [ ] 2.2 Replace `msg.Audience ?? TrustAudience.Personal.ToWireValue()` in `SubAgentActor` with an explicit `throw` on a missing audience.
- [ ] 2.3 Replace `?? new NullPromptInjectionDetector()` in `SlackThreadBindingActor` and `SlackChannel` with an explicit `throw` on a missing detector.
- [ ] 2.4 Add or update tests asserting each site throws on the missing-context path (use `Ask`/`AwaitAssert` patterns; no `Thread.Sleep`).
- [ ] 2.5 Verify PR-B: build clean, tests green, slopwatch clean, file headers verified.

## 3. PR-C — `ToolExecutionContext` / `RunSubAgent` audience typing

- [ ] 3.1 Change `ToolExecutionContext.Audience` (`Netclaw.Tools.Abstractions`) from `string?` to `TrustAudience?`.
- [ ] 3.2 Update the write sites that build `ToolExecutionContext` (`SessionToolExecutionPipeline`, `SubAgentActor`) to parse the audience to `TrustAudience` at construction.
- [ ] 3.3 Update read sites (`SpawnAgentTool`, `ToolAccessPolicy.ResolveAudience`, `CheckBackgroundJobTool`, `ScopedFileAccessPolicy` profile resolution) to consume the typed audience.
- [ ] 3.4 Change `RunSubAgent.Audience` (`Netclaw.Actors/SubAgents/SubAgentProtocol.cs`) from `string?` to `TrustAudience?`.
- [ ] 3.5 Delete `SecurityPolicyDefaults.ParseAudienceOrPublic` and `ResolveAudienceWithFallback` once they are dead on the read path; fix any remaining callers.
- [ ] 3.6 Update affected tests for the typed audience.
- [ ] 3.7 Verify PR-C: build clean, tests green, slopwatch clean, file headers verified; run `./evals/run-evals.sh` (tool-definition change).

## 4. PR-D — Persisted records + loud JSON converter + doctor check

- [ ] 4.1 Add a shared `RequiredFieldConverter`-style JSON converter helper (`Netclaw.Configuration/Json/` or `Netclaw.Actors/Persistence/`) that throws, naming the document and missing field, when a `required` trust field is absent in a legacy document.
- [ ] 4.2 Make `BackgroundJobDefinition.Audience`/`Boundary` (`Netclaw.Actors/Jobs/BackgroundJobProtocol.cs`) `required`; wire the converter.
- [ ] 4.3 Make `ActiveJobInfo.Audience`/`Boundary` (`Netclaw.Actors/Jobs/ActiveJobInfo.cs`) `required`; wire the converter.
- [ ] 4.4 Make `ReminderDefinition.Audience`/`Boundary` (`Netclaw.Actors/Reminders/ReminderProtocol.cs`) `required` and non-nullable; wire the converter.
- [ ] 4.5 Add a `netclaw doctor` check (`Netclaw.Cli/Doctor/`) that scans the persistence directory for legacy job/reminder documents missing trust fields and reports them.
- [ ] 4.6 Implement `netclaw doctor --fix` backfill that writes an explicit conservative (`Public` / public boundary) value after operator confirmation.
- [ ] 4.7 Add tests: legacy document fails loud on deserialize; doctor check detects affected documents; `--fix` backfills conservative values.
- [ ] 4.8 Verify PR-D: build clean, tests green, slopwatch clean, file headers verified; run `./evals/run-evals.sh` (persistence/config change).

## 5. Cross-cutting verification and documentation

- [ ] 5.1 Manual smoke: restart the daemon with the Personal-DM Slack configuration from PR #993; confirm `shell_execute` is permitted (no Public downgrade).
- [ ] 5.2 Manual smoke: attempt to deserialize a known legacy `*.job.json` / reminder document; confirm the converter fails loud and `netclaw doctor` reports it.
- [ ] 5.3 Update operator-facing docs / runbook with the upgrade note for legacy persisted documents and the `netclaw doctor --fix` remediation path.
- [ ] 5.4 Run `/opsx-verify` against this change, then `/opsx-sync` and `/opsx-archive`.
