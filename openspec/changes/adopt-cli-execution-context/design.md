## Context

Baseline: `upstream/dev` at `aa5fc525a`.
See [proposal.md](proposal.md) for the problem and scope.
The CLI has host services, but offline commands can execute without a host.
`pair`, `update`, and `approvals` already receive several execution environment values.
No existing invocation type owns all five values.

## Goals / Non-Goals

The first slice removes process defaults from these three command entry points.
It preserves their authentication, cancellation, approval, and stored-data contracts.
It does not migrate every command or change workspace resolution.
It does not add configuration to the context.

## Decisions

Use an internal sealed record with five required members:

- `NetclawPaths Paths`
- `TimeProvider Time`
- `TextReader Input`
- `TextWriter Output`
- `TextWriter Error`

The record holds references. It does not own or dispose streams.
`Program.cs` owns composition and selects the command.
Offline branches create their context from the paths that the branch already owns.
Host branches resolve a singleton context from their final path and clock registrations.
The factory resolves dependencies after all registrations, so the init branch cannot leave the context with an earlier paths instance.
Inner services retain their specific dependencies.
The host keeps its existing workspace resolution behavior in this first slice.
A future path slice must resolve configured workspace paths before it constructs its context.

The invocation owns the context for its process lifetime.
Command arguments, cancellation tokens, HTTP clients, and update policy remain explicit parameters.
The update helpers receive the streams or clock that their operation needs.
The systemd owner check receives the invocation home.
The context introduces no actor message, actor state, or durable record.
File writers and daemon consumers retain their existing canonical representations.

```text
Program -> existing paths and clock registrations
  offline command: construct its context
  host command: resolve its context from final registrations
context -> pair / update / approvals
  pair -> explicit HTTP client and cancellation token -> existing protected file writers
  update -> manifest verification -> supplied output and error streams
  update -> daemon manager(paths, clock) -> systemd owner check(home)
  approvals -> store(paths, clock) -> existing approval decisions
```

This flow is schematic. It omits endpoint validation and file-write protection steps.

Keep the existing individual-parameter helpers where callers use them independently.
Replace the command signatures and update their callers instead of adding compatibility overloads.
The existing DI container cannot serve offline branches, which justifies this small explicit record.

## Risks / Trade-offs

- A captured output stream can disrupt a TUI. TUI components retain their own terminal services.
- A command can bypass its supplied clock or streams. A narrow source guard covers the three adopted entry points.
- A refactor can change cancellation. Existing pair timeout and caller-cancellation tests cover the same command path.
- An update can inspect another home. A custom-home ownership test verifies that the update factory uses the supplied paths.
- Shared host registrations can affect init. Existing headless tests and the native smoke harness verify host composition.

## Migration Plan

Deliver one reviewable local change with migrated tests and the preserved public CLI contract.
No file migration or operator action is required.
Rollback uses the prior binary and the same stored files.
