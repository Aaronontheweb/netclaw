## MODIFIED Requirements

### Requirement: Loopback authentication scheme

The daemon SHALL register a loopback authentication scheme that automatically authenticates connections from `127.0.0.1` and `::1` as `LocalProcess` / `Operator` without requiring credentials only when the selected exposure mode allows loopback trust.

When `Daemon.ExposureMode` is `reverse-proxy`, the loopback scheme SHALL return no result for loopback requests so that only explicit credentialed schemes can authorize the connection.

In `local` exposure mode, the loopback scheme SHALL refuse a loopback-source request unless the request passes both of these tests:

- The `Host` header names `localhost`, `127.0.0.1`, or `[::1]`, with any port, compared without regard to case.
- The request has no `Origin` header, or the `Origin` header equals `http://` plus the request `Host`, compared without regard to case.

The loopback scheme SHALL fail a refused request with a reason, and the route SHALL answer HTTP 401. The loopback scheme SHALL log each refusal at warning level with the `Host` and `Origin` values and no other request data. The rule SHALL be fixed and SHALL have no configuration option. The Host and Origin test SHALL NOT apply to requests that carry a bearer token or to routes that allow anonymous access.

#### Scenario: Loopback connection auto-authenticated in local mode

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1` or `::1`
- **AND** the `Host` header is `localhost:5199`
- **WHEN** the client connects to `/hub/session`
- **THEN** the connection is authenticated with principal classification `Operator` and transport authenticity `LocalProcess`

#### Scenario: Matching Origin is accepted

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `localhost:5199` and the `Origin` header is `http://localhost:5199`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the connection is authenticated as `Operator` with `LocalProcess` authenticity

#### Scenario: Foreign Host is refused

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `rebind.example:5199`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** authentication fails
- **AND** the route answers HTTP 401

#### Scenario: Foreign Origin is refused

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `127.0.0.1:5199` and the `Origin` header is `http://evil.example`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** authentication fails
- **AND** the route answers HTTP 401

#### Scenario: Null Origin is refused

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Origin` header is `null`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** authentication fails

#### Scenario: Bearer request is not checked

- **GIVEN** a request carries a valid device bearer token
- **AND** the `Host` header is `app.example.com`
- **WHEN** the daemon authenticates the request
- **THEN** the bearer scheme authenticates the request
- **AND** the loopback Host and Origin test does not run

#### Scenario: Non-loopback connection not auto-authenticated

- **GIVEN** a connection originates from a non-loopback address
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the scheme returns no result (defers to other schemes)

#### Scenario: Reverse-proxy mode does not auto-authenticate loopback

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** a connection originates from `127.0.0.1`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the scheme returns no result
- **AND** the connection must authenticate through a credentialed scheme instead
