## ADDED Requirements

### Requirement: Chat output contracts have deterministic headless proof

Headless tests SHALL inject every supported `SessionOutput` type through the
chat presentation boundary. Tests SHALL verify stable identity, lifecycle,
settlement, complete detail, and responsive layout without a live provider.

#### Scenario: Parallel tools complete out of order

- **GIVEN** headless chat receives tool starts A, B, and C
- **WHEN** results arrive in the order B, C, and A
- **THEN** snapshots show three distinct matching results
- **AND** no result replaces an unrelated row

#### Scenario: Every output type has a disposition

- **WHEN** the output contract test enumerates the supported `SessionOutput`
  union
- **THEN** every type maps to a visible, deliberately hidden, or security-
  filtered presentation disposition
- **AND** an unclassified type fails the test

#### Scenario: Responsive snapshot matrix

- **WHEN** representative active and settled Turns render at 40, 60, 80, and
  120 columns
- **THEN** no unrelated events share one line
- **AND** required identity and lifecycle state remain visible

### Requirement: Chat input contracts have typed-key proof

Headless typed-key tests SHALL cover submit, `Shift+Enter`, prompt history,
draft restoration, double Escape, approval denial, `Ctrl+O`, detail scroll, and
multiline paste. Time-based key sequences SHALL use `TimeProvider`.

#### Scenario: Shift Enter does not submit

- **WHEN** the test enters text and sends `Shift+Enter`
- **THEN** the input contains one newline
- **AND** the submit observer receives no value

#### Scenario: Approval detail preserves selection

- **GIVEN** Allow once is selected
- **WHEN** the test sends `Ctrl+O`, PageDown, and `Ctrl+O`
- **THEN** the detail expands, moves, and collapses
- **AND** Allow once remains selected
- **AND** no approval response occurs before Enter

#### Scenario: Double Escape uses virtual time

- **GIVEN** a nonempty recalled prompt
- **WHEN** the test sends two Escape keys inside the configured virtual-time
  window
- **THEN** the input clears without `Task.Delay` or `Thread.Sleep`

### Requirement: Native chat smoke proves the terminal contract

The native smoke harness SHALL run the real published CLI and SHALL prove chat
startup, typed input, multiline input, paste, approval detail, prompt recall,
double Escape, resize, interruption, and clean shutdown.

The Termina dependency SHALL provide separate native proof for primary-buffer
scrollback, native selection, Linux, macOS, Windows Terminal, and tmux.

#### Scenario: Native inline chat flow

- **WHEN** the `netclaw chat` native tape runs against a deterministic daemon
- **THEN** the tape proves the primary chat flow through semantic anchors
- **AND** each nontrivial action has an assertion
- **AND** the tape uses no fixed sleep

#### Scenario: Full-screen regression suite

- **WHEN** Termina inline support enters the Netclaw dependency graph
- **THEN** the existing init, config, model, provider, and approval TUI smoke
  flows retain their full-screen behavior
- **AND** `./scripts/smoke/run-smoke.sh light` passes
