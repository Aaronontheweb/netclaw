## MODIFIED Requirements

### Requirement: SessionTuning for internal constants

The system SHALL represent internal tuning constants in a `SessionTuning` record
nested inside `SessionConfig` as `SessionConfig.Tuning`. Properties SHALL include
compaction settings (`CompactionThreshold`, `KeepRecentToolResults`,
`KeepRecentMessages`, `CompactionModelId`), tool retention settings
(`DiscoveredToolRetentionTurns`, `DiscoveredToolMaxCount`, `MaxInlineToolResultChars`),
snapshot interval (`SnapshotInterval`), title generation interval
(`TitleGenerationInterval`), and turn-loop governance settings
(`UnproductiveIterationLimit`). Feature flags (`MemorySidecarsEnabled`,
`DeterministicRetrievalEnabled`) SHALL be included for backward compatibility with
intent to remove.

#### Scenario: SessionTuning defaults match current production values

- **WHEN** a default `SessionTuning` is constructed
- **THEN** `CompactionThreshold` is 0.75
- **AND** `SnapshotInterval` is 20
- **AND** `KeepRecentToolResults` is 3
- **AND** `MaxInlineToolResultChars` is 12,000
- **AND** `DiscoveredToolRetentionTurns` is 3
- **AND** `DiscoveredToolMaxCount` is 12
- **AND** `KeepRecentMessages` is 6
- **AND** `TitleGenerationInterval` is 10
- **AND** `UnproductiveIterationLimit` is 3
- **AND** `MemorySidecarsEnabled` is true
- **AND** `DeterministicRetrievalEnabled` is true

#### Scenario: SessionTuning bindable from config for testing

- **GIVEN** `netclaw.json` contains `"Session": { "Tuning": { "SnapshotInterval": 5 } }`
- **WHEN** configuration is bound
- **THEN** `SessionConfig.Tuning.SnapshotInterval` is 5
- **AND** all other tuning properties retain defaults

### Requirement: Slimmed SessionConfig with TimeSpan timeouts

The system SHALL represent user-facing operational settings in `SessionConfig` using
`TimeSpan` properties for timeouts (`TurnLlmTimeout`, `ToolExecutionTimeout`,
`SidecarLlmTimeout`) instead of `int` seconds. Config-file JSON keys SHALL remain
as `XxxTimeoutSeconds` (int) for user-facing backward compatibility. A static bind
method SHALL convert from the raw int-seconds JSON representation to `TimeSpan`,
enforcing a minimum of 1 second per timeout.

#### Scenario: TimeSpan conversion from config file

- **GIVEN** `netclaw.json` contains `"Session": { "TurnLlmTimeoutSeconds": 120 }`
- **WHEN** `SessionConfig` is bound from configuration
- **THEN** `SessionConfig.TurnLlmTimeout` is `TimeSpan.FromSeconds(120)`

#### Scenario: Minimum timeout enforcement

- **GIVEN** `netclaw.json` contains `"Session": { "SidecarLlmTimeoutSeconds": 0 }`
- **WHEN** `SessionConfig` is bound from configuration
- **THEN** `SessionConfig.SidecarLlmTimeout` is `TimeSpan.FromSeconds(1)`

#### Scenario: Default SessionConfig values

- **WHEN** a default `SessionConfig` is constructed
- **THEN** `IdleTimeout` is 30 minutes
- **AND** `MaxToolIterationsPerTurn` is 60
- **AND** `MemoryObserverIdleSeconds` is 90
- **AND** `TurnLlmTimeout` is 3 minutes
- **AND** `ToolExecutionTimeout` is 90 seconds
- **AND** `SidecarLlmTimeout` is 90 seconds

### Requirement: JSON schema validation for Session section

The `netclaw-config.v1.schema.json` Session section SHALL use
`additionalProperties: false` with explicit property definitions. The schema SHALL
include a nested `Tuning` object for internal constants. Unknown properties in the
Session section SHALL be rejected by schema validation.

#### Scenario: Valid Session config passes schema validation

- **GIVEN** a `netclaw.json` with `"Session": { "MaxToolIterationsPerTurn": 50 }`
- **WHEN** schema validation runs
- **THEN** validation passes

#### Scenario: Unknown Session property rejected

- **GIVEN** a `netclaw.json` with `"Session": { "FakeProperty": true }`
- **WHEN** schema validation runs
- **THEN** validation fails with an error identifying the unknown property
