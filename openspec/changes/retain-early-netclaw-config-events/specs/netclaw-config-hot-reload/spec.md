Terms follow the [engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## MODIFIED Requirements

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
