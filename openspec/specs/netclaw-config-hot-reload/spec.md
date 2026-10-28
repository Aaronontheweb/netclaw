# netclaw-config-hot-reload Specification

## Purpose

Define file-event reload behavior for `netclaw.json`, from configuration load through host shutdown.
Valid candidates use the existing coordinated daemon restart.
Terms follow the [engineering glossary](../../../docs/spec/GLOSSARY.md).

## Requirements

### Requirement: Operational config file monitoring

The daemon SHALL monitor `netclaw.json` before it loads configuration for each host generation.
Changed, Created, and rename-into events SHALL request reload validation.
The daemon SHALL retain an event until the hosted service can accept it.

#### Scenario: Config edit during startup

- **GIVEN** the daemon loads its configuration before hosted service startup
- **WHEN** `netclaw.json` changes in that interval
- **THEN** the daemon validates the latest canonical sources after application startup
- **AND** a valid candidate requests coordinated restart

#### Scenario: Unwatched files are not monitored

- **WHEN** only `secrets.json` or another unwatched file changes
- **THEN** the daemon creates no reload request from that event

#### Scenario: Deleted file creates no new request

- **WHEN** `netclaw.json` is deleted or renamed away
- **THEN** that event creates no new reload request
- **AND** it does not cancel an earlier retained change event

### Requirement: Change event debounce

The daemon SHALL debounce reload signals with a fixed 500ms interval.
Several signals before hosted service attachment SHALL coalesce into one retained signal.

#### Scenario: Rapid successive writes debounced

- **WHEN** several watched-file events occur within 500ms
- **THEN** the daemon processes one reload after the last signal's debounce interval

#### Scenario: Startup without an event

- **WHEN** the hosted service starts without a retained file event
- **THEN** startup alone creates no reload work

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

### Requirement: ConfigWatcherService hosted service

Each host generation SHALL own one file watcher from before configuration load through host disposal.
The hosted service SHALL accept retained events and wait for application startup before validation.
Service stop SHALL prevent further event delivery and cancel pending reload work.

#### Scenario: Service starts with application

- **WHEN** the hosted service attaches to the early watcher
- **THEN** it accepts any retained signal once
- **AND** it defers validation until application startup completes

#### Scenario: Service stops on shutdown

- **WHEN** the hosted service stops
- **THEN** later file events cannot request a reload through that service
- **AND** the host generation disposes its native watcher

#### Scenario: Host creation fails

- **WHEN** configuration load or host creation fails after the watcher starts
- **THEN** the host generation disposes the watcher before it exits
