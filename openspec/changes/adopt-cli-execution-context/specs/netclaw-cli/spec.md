## ADDED Requirements

### Requirement: CLI command execution uses its supplied environment

The `pair`, `update`, and `approvals` command paths SHALL use their supplied home, clock, input, output, and error streams.
Their inner operations SHALL retain those values when they need them.
The host SHALL use the same paths and clock for the command environment and its inner services.
The command SHALL retain its existing endpoint validation, authentication, cancellation, and approval rules.
The command SHALL preserve its current exit numbers and persisted representations.

#### Scenario: Pair writes only to the supplied home

- **GIVEN** the pair command receives a temporary home and an HTTP exchange client
- **WHEN** the remote daemon accepts the code
- **THEN** the command stores the token and endpoint under that home
- **AND** it reads input and writes output through the supplied streams

#### Scenario: Pair timeout follows the supplied clock

- **GIVEN** the pair command receives a virtual clock and an exchange that does not complete
- **WHEN** that clock reaches the request deadline
- **THEN** the command returns its timeout failure through the supplied error stream
- **AND** it writes no token or endpoint

#### Scenario: Invalid pair endpoint writes no client state

- **GIVEN** the pair command receives a non-loopback HTTP endpoint
- **WHEN** the command validates the endpoint
- **THEN** it rejects the endpoint before the HTTP exchange
- **AND** it writes no token or endpoint

#### Scenario: Approval creation uses the supplied clock

- **GIVEN** the approvals command receives a temporary home and a virtual clock
- **WHEN** the operator adds a valid grant
- **THEN** the stored grant uses that clock for its creation time
- **AND** existing approval rules determine the grant representation

#### Scenario: Update diagnostics use the supplied streams

- **GIVEN** the update command receives separate output and error streams
- **WHEN** an asset download or checksum verification fails
- **THEN** it reports the failure through those supplied streams
- **AND** it returns exit code 1 before a binary replacement

#### Scenario: Update inspects its own home

- **GIVEN** the update command receives a non-default home
- **AND** the systemd user unit serves a different home
- **WHEN** the command checks the daemon owner
- **THEN** it does not claim that the unit owns the invocation home
