## Why

A config edit can occur after configuration load and before the hosted watcher starts.
PR #2713 must close this gap without a credential comparison subsystem.

## What Changes

- Arm one file watcher before configuration load and retain early `netclaw.json` events.
- Remove the generation comparator and its credential projection rules.
- Preserve canonical validation, source precedence, debounce, and coordinated restart.
- Exclude secret-file-only changes from this fix.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-config-hot-reload`: define the watched file, early event handoff, and lifecycle ownership.

## Impact

Source requirement: [PRD-001 FR-016](../../../docs/prd/PRD-001-netclaw-mvp.md#fr-016-config-change-restart-coordination).
Terms follow the [engineering glossary](../../../docs/spec/GLOSSARY.md).
Program owns the native watcher; the hosted service owns validation and debounce.
The restart coordinator retains its current contract.

### Security and operational impact

Invalid candidates still fail before a restart request.
The watcher adds no authority or config source.
A `netclaw.json` edit triggers canonical validation, including the secrets overlay.
A secret-only OAuth refresh emits no reload signal.
