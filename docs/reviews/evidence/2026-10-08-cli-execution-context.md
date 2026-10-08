# CLI execution context: local verification

This is a structural refactor for `pair`, `update`, and `approvals`.
The context contains paths, clock, input, output, and error streams.
The caller owns streams. Inner services retain their specific dependencies.
No public CLI option, exit code, configuration shape, or stored-data format changes.

## Local evidence

The initial implementation used `upstream/dev` at `aa5fc525a`.
These results apply to implementation commit `6ad420a3d`:

- All 172 focused command, context, and host tests passed, with no skips.
- All 2,034 CLI tests passed with an isolated `NETCLAW_HOME`, with no skips.
- The native light smoke harness passed 27 tapes and ten daemon scenarios.
- Slopwatch reported zero new issues. Copyright headers and the diff check passed.

The operator environment exposed one existing wizard test failure.
The unchanged base fails `RunWithOrchestrator_SupervisorMarkerSetButNoSupervisor_SurfacesActionableReason` too.
The test's default systemd probe sees the host's active unit instead of its fake supervisor.
The isolated home removes that host dependency. This refactor does not change the wizard test.

The update tests verify HTTP and checksum failures before daemon access.
They also verify the supplied home and clock, foreign-unit refusal, and binary rollback.
The approval tests verify canonical grants, diagnostics, and the supplied timestamp.
The pair tests retain endpoint rejection, bounded responses, virtual deadlines, and caller cancellation.
The source guard rejects process defaults in these three commands.
No authorization gate or owner rule changes. Existing focused Stryker targets remain unchanged.

Behavioral evals are not required for this CLI refactor, per the operator's instruction.
The coverage attempt passed 61 update tests, but no coverage collector was available.
No OpenCover report or CRAP score exists.

Other command migrations and shared path construction remain outside this slice.

