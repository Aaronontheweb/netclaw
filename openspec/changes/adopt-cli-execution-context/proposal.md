## Why

CLI commands choose their own streams and clocks, which makes tests and daemon operations depend on the process environment.
One explicit execution environment removes these defaults from the commands that already need several of these values.

Source PRDs: [PRD-004](../../../docs/prd/PRD-004-cli-onboarding-and-config.md) and [PRD-002](../../../docs/prd/PRD-002-gateway-security-envelope.md).

## What Changes

- Add an internal `CliContext` with paths, clock, input, output, and error streams.
- Adopt the context in `pair`, `update`, and `approvals` as the first slice.
- Register the context through the existing CLI host services.
- Resolve the context paths and clock from the same instances that inner services use.
- Route update progress and diagnostics through the supplied streams.
- Pass the update invocation home to the systemd service owner check.
- Preserve command arguments, exit numbers, persisted representations, and cancellation behavior.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-cli`: Specify the explicit execution environment for these three command entry points.

## Impact

The change affects CLI command entry points, the host composition root, and their tests.
It adds no dependency or configuration property.
Other command migrations and global path construction remain separate slices.
Serializer consolidation, culture changes, and new exit numbers remain outside this slice.
See the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

## Security and Operational Impact

Existing daemon authentication, approval rules, manifest verification, and file protection remain in force.
The systemd owner check uses the update invocation home instead of an implicit default home.
The context contains no service provider, command policy, daemon client, or permission grant.
The native smoke harness verifies the host composition root because TUI branches share it.
