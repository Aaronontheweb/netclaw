# Verification: adopt-cli-execution-context

## Scope

Base: `upstream/dev` at `aa5fc525a`.
Branch: `refactor/cli-execution-context`.
This report covers the first slice only: `pair`, `update`, and `approvals`.
The primary checkout remains unchanged.
No PR, remote push, merge, or deployment applies.

## Contract Evidence

| Scenario | Evidence |
| --- | --- |
| Pair writes to its supplied home | `PairCommandTests.SuccessfulExchange_SavesTokenAndEndpoint`; `RemotePairingSignalRIntegrationTests` also verifies the stored token against the daemon. |
| Pair uses its virtual deadline | `PairCommandTests.Timeout_FailsWithoutSavingClientState` and `ResponseDeadline_IncludesElapsedHeaderTimeWithoutSavingClientState`. |
| Invalid pair endpoint changes no client state | `PairCommandTests.NonLoopbackHttpEndpoint_FailsBeforeCodeInput`. |
| Approval creation uses its virtual clock | `ApprovalsCommandTests.TrustVerb_adds_global_wildcard_with_default_audience_and_tool` asserts the stored timestamp. |
| Update reports asset failures through supplied streams | `UpdateCommandTests.RunAsync_ReportsAssetFailureToSuppliedOutput_BeforeDaemonChange` covers HTTP failure and checksum failure. |
| Update uses its supplied home and clock | `UpdateCommandTests.RunAsync_UsesSuppliedHomeAndClock_AndDoesNotStopAnotherHomesUnit` verifies factory inputs and refuses a foreign unit. |
| Host composition supports a custom home without a daemon | `DaemonCommandWiringTests.Update_help_resolves_the_host_context_for_the_supplied_home_without_a_daemon`. |

The context has five required references and no service provider.
The caller owns streams. TUI components keep their terminal services.
Inner services retain their specific dependencies.
The update factory passes `paths.BasePath` to the production systemd probe.
The source guard rejects a parameterless systemd probe in this command.

## Local Checks

- The initial focused baseline passed 152 tests.
- The final focused command, context, and host run passed 172 tests, with no skips.
- The full CLI run under the operator environment passed 2,033 tests and failed one test, with no skips.
- The unchanged base fails that same wizard test in an independent worktree.
- The full CLI run with an isolated `NETCLAW_HOME` passed all 2,034 tests, with no skips.
- The native light smoke harness passed all 27 tapes and ten daemon scenarios.
- OpenSpec validates both the change and the main `netclaw-cli` specification.
- Slopwatch reports zero new issues.
- Copyright-header verification passes.
- The diff check passes.

The coverage attempt passed all 61 update tests but found no `XPlat Code Coverage` collector.
This project does not reference a coverage collector.
No OpenCover report or CRAP score is available.
The contract evidence above covers behavior, not a coverage percentage.

The wizard failure is `RunWithOrchestrator_SupervisorMarkerSetButNoSupervisor_SurfacesActionableReason`.
The test expects its fake supervisor reason.
Its default systemd probe sees this host's active and enabled `netclaw.service` instead.
The test does not use the CLI host context.
The refactor does not change its code or `StartDaemonAsync`.
The isolated run confirms the host dependency and passes the full suite.
The unisolated run is not green.

Local logs use the `/tmp/netclaw-cli-context-` prefix.

## Mutation Scope Review

No authorization gate, shell phrase rule, or daemon owner rule changes.
The refactor changes the supplied environment, not those decisions.
The existing focused Stryker targets remain unchanged.
`CliContextGuardTests` rejects process defaults in the three adopted command files.
Its positive and negative cases cover paths, clocks, streams, and the systemd default.
This source guard is not a Stryker result.

## Remaining Gate

The operations skill version changes from `2.105.0` to `2.105.1`.
The required behavioral eval suite cannot select an unconfigured provider target.
`./evals/run-evals.sh < /dev/null` fails before it creates a daemon or runs a case.
It requires `NETCLAW_EVAL_PROVIDER_TYPE`, `NETCLAW_EVAL_PROVIDER_ENDPOINT`, and `NETCLAW_EVAL_MODEL_ID`.
No operator waiver applies yet.
The change remains active until the operator supplies a target or waives this gate.

## Final Assessment

| Dimension | Status |
| --- | --- |
| Completeness | Eight of nine tasks are complete. The behavioral eval gate remains open. |
| Correctness | Tests cover all six specification scenarios. The full CLI suite and native smoke harness pass with an isolated home. |
| Coherence | The implementation follows the five-member context design. Existing services retain their specific dependencies. |

The missing eval target prevents archive and full completion under the repository quality rules.
The source change is ready for local review.
The host-dependent wizard test remains a separate issue under the operator environment.
The report includes no hosted CI, Windows, or release claim.

## Deferred Scope

Other command migrations and shared path construction remain separate slices.
Serializer consolidation, culture changes, and exit-number changes remain outside this slice.
