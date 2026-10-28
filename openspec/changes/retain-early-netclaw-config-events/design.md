## Context

See [the proposal](proposal.md) for scope and [the glossary](../../../docs/spec/GLOSSARY.md) for terms.
The old generation comparator adds credential rules to a file-event gap fix.

## Goals / Non-Goals

Retain early events with one host-generation-local owner.
Do not add durable state, actor protocols, credential comparisons, or secret-only reload behavior.

## Decisions

Program owns `ConfigFileWatch` through a `using` scope before canonical configuration load.
It registers the same instance; the hosted service only attaches and detaches.
One lock protects the callback, retained-event bit, and terminal detach state.
Attach drains the bit once; later events call the short debounce callback under that lock.
The lock order is watcher lock, then service debounce lock.
Stop detaches before it cancels debounce work; native disposal occurs outside the watcher lock.
Required dependencies remain required.

Schematic flow:

```text
arm watcher -> load canonical config -> create host -> attach service
file event -> retain bit or schedule debounce
pending reload -> await ApplicationStarted -> validate canonical sources
invalid -> log rejection and retain host
valid -> existing restart coordinator -> existing session drain and recovery
stop -> detach -> cancel pending work -> dispose host -> dispose watcher
```

The service retains source precedence, JSON checks, model checks, and provider checks.
The restart coordinator remains the actor and persistence boundary owner.
The early watcher stores no durable data.
For example, a startup `netclaw.json` host edit reaches the next host.
A secret-only OAuth refresh creates no event signal.

## Risks / Trade-offs

- A native watcher start failure aborts startup visibly.
- Duplicate native events use the existing debounce interval.
- Secret-only startup edits need a separate contract if operators later require them.
- A pending change survives deletion; canonical validation decides the resulting candidate.

## Migration Plan

No config or persistence migration applies.
A rollback restores the prior binary and its prior event behavior.
