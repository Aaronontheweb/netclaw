# netclaw-config-hot-reload Specification

## Purpose

Define file-event reload behavior for `netclaw.json`, from configuration load through host shutdown.
Valid candidates use the existing coordinated daemon restart.
Terms follow the [engineering glossary](../../../docs/spec/GLOSSARY.md).

## Requirements

### Requirement: Operational config file monitoring

The daemon SHALL monitor `netclaw.json` through one actor per host generation.
Changed, Created, and rename-into events SHALL become actor messages.
After watcher startup, the actor SHALL compare disk with the hash of the exact file bytes its host loaded.
A different hash for a present file SHALL request validation after application startup.
A missing current file SHALL create no startup reload request.

#### Scenario: Config edit during startup

- **WHEN** `netclaw.json` changes after configuration load and before watcher startup
- **THEN** the hash check detects the edit
- **AND** the actor validates it after application startup

#### Scenario: File disappears during startup

- **WHEN** the loaded file is absent after watcher startup
- **THEN** the actor keeps the current host without a startup reload request

#### Scenario: Unwatched files are not monitored

- **WHEN** only `secrets.json` or another unwatched file changes
- **THEN** the daemon creates no reload request from that event

#### Scenario: Deleted file creates no new event request

- **WHEN** a native event reports deletion or rename-away
- **THEN** that event creates no reload request

### Requirement: Change event debounce

The actor SHALL debounce config messages with one keyed 500ms timer.
A later config message SHALL reset that timer.
An unchanged startup file hash SHALL cause no startup restart request.

#### Scenario: Rapid successive writes debounced

- **WHEN** several config messages arrive within 500ms
- **THEN** validation occurs after the last message's debounce interval

#### Scenario: Startup with unchanged bytes

- **WHEN** the loaded file hash matches disk after watcher startup
- **THEN** startup alone creates no restart request

### Requirement: Validate before apply

The daemon SHALL validate changed configuration before runtime recovery.
The candidate SHALL use the same canonical source precedence as host startup.
Invalid configuration SHALL retain the current effective configuration and produce diagnostics.
Valid configuration SHALL initiate coordinated restart.

#### Scenario: Valid config change initiates coordinated restart

- **WHEN** a watched-file event produces a valid candidate
- **THEN** the daemon closes new session ingress
- **AND** it starts session drain before host shutdown

#### Scenario: Invalid config change rejected

- **WHEN** a watched-file event produces an invalid candidate
- **THEN** the current daemon continues with its previous effective configuration
- **AND** the daemon logs the rejection

#### Scenario: Secrets overlay retains precedence

- **WHEN** `netclaw.json` changes and a secrets overlay supplies a value
- **THEN** validation and the next host use the canonical overlay value

### Requirement: Coordinated daemon restart on valid config change

The system SHALL coordinate valid config changes through a restart coordinator.
The coordinator SHALL capture the set of currently active sessions after ingress
is closed, wait for those sessions to drain or time out, persist restart
recovery state, and only then request daemon shutdown.

#### Scenario: Active sessions drain before restart

- **GIVEN** one or more sessions are active when a valid config change is detected
- **WHEN** restart coordination begins
- **THEN** the coordinator waits for drain completion acknowledgements from those sessions
- **AND** requests daemon shutdown only after all recorded sessions drain or the timeout expires

#### Scenario: Incoming work rejected during restart drain

- **GIVEN** the daemon has entered restart drain mode
- **WHEN** a new inbound message arrives through any daemon-managed adapter
- **THEN** the message is rejected with a restart-in-progress response
- **AND** no new session actor is created for that message

#### Scenario: Active session stops its turn at the next safe point during restart drain

- **GIVEN** a session is already in `Processing` or `Compacting` when restart drain begins
- **WHEN** the coordinator requests drain for that session
- **THEN** the session rejects new work
- **AND** stops the turn at the next safe point: a live model call after the 2-second grace period, the end of a tool batch, or the end of a compaction
- **AND** only then passivates and acknowledges the drain

#### Scenario: Drain timeout still requests restart

- **GIVEN** at least one recorded active session does not drain before the restart timeout
- **WHEN** the timeout expires
- **THEN** the coordinator requests daemon shutdown anyway
- **AND** the shutdown alert names each timed-out session

### Requirement: Config reload actor lifecycle

The actor SHALL own and dispose its native watcher.
It SHALL defer validation until application startup and admit at most one coordinated restart request at a time.
It SHALL retain the existing validation, source precedence, and rejection contracts.

#### Scenario: Events before application startup

- **WHEN** the debounce interval ends before application startup
- **THEN** the actor retains the pending request until startup completes

#### Scenario: Actor stops

- **WHEN** the actor stops
- **THEN** it disposes its watcher and cancels its pending coordinator call
