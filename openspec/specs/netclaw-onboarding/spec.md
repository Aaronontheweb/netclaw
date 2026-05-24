## Purpose

Define the bootstrap-first `netclaw init` onboarding flow, its existing-install
branches, and the required validation and identity-file behavior for a runnable
baseline setup.

## Requirements

### Requirement: Guided onboarding

The CLI SHALL provide bootstrap-first guided setup through `netclaw init`.
The onboarding wizard SHALL collect provider configuration, identity, and
security posture, then write a runnable baseline configuration. On
completion, the wizard SHALL run a health check to verify the baseline
configuration is functional. If daemon startup fails because configuration
validation rejects the selected exposure mode or remote-auth topology, the
wizard SHALL surface that failure as a structured setup error with
remediation guidance.

Security Posture, Enabled Features, and Audience Profiles are distinct
concepts.

If the operator selects `Personal`, the bootstrap flow SHALL skip Enabled
Features.

If the operator selects `Team` or `Public`, the bootstrap flow SHALL
automatically continue into Enabled Features before final write.

Audience Profiles editing SHALL NOT be part of init bootstrap; it belongs to
`netclaw config`.

The wizard SHALL NOT write `AGENTS.md` to disk during identity file
generation. AGENTS.md is binary-controlled firmware loaded from embedded
resources at runtime. The wizard SHALL continue to write `SOUL.md` and
`TOOLING.md` as operator-mutable identity files. Identity remains init-owned.

For non-Personal postures, the Enabled Features step writes deployment-wide
`Enabled` switches. These switches SHALL NOT implicitly rewrite Public
audience allowlists.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects provider, identity, and security posture
  inputs
- **AND** writes a runnable baseline configuration
- **AND** writes SOUL.md and TOOLING.md to `~/.netclaw/identity/`
- **AND** does NOT write AGENTS.md (or writes a reference-only stub)

#### Scenario: Personal posture skips enabled-features bootstrap step

- **GIVEN** the operator selected `Personal`
- **WHEN** the posture step completes
- **THEN** init does not open an Enabled Features step

#### Scenario: Team posture continues into enabled-features bootstrap step

- **GIVEN** the operator selected `Team`
- **WHEN** the posture step completes
- **THEN** init automatically continues into Enabled Features

#### Scenario: Public posture continues into enabled-features bootstrap step

- **GIVEN** the operator selected `Public`
- **WHEN** the posture step completes
- **THEN** init automatically continues into Enabled Features

#### Scenario: Identity files written on completion

- **WHEN** the wizard completes and writes config
- **THEN** `SOUL.md` is written from the embedded SOUL template
- **AND** `TOOLING.md` is written from the embedded TOOLING template
- **AND** `AGENTS.md` is NOT written from a template

#### Scenario: Public posture defaults search off without mutating Public tool allowlist

- **GIVEN** the operator selected Public posture
- **WHEN** the Feature Selection step is shown
- **THEN** Search defaults to disabled
- **AND** enabling Search there affects only the deployment-wide runtime switch
- **AND** `Tools.AudienceProfiles.Public.AllowedTools` is not implicitly widened

#### Scenario: Exposure-mode startup validation failure shown cleanly

- **GIVEN** the operator completes `netclaw init`
- **AND** the written configuration causes `ExposureModeValidationService` to reject
  daemon startup
- **WHEN** the health-check step starts the daemon
- **THEN** the wizard shows a failed health-check item containing the validation
  message
- **AND** the wizard includes remediation guidance for fixing the exposure/auth
  configuration
- **AND** the operator is not shown a raw stack trace

#### Scenario: Startup validation failure does not degrade to generic readiness timeout

- **GIVEN** daemon startup fails immediately because exposure validation rejects the
  configuration
- **WHEN** the health-check step polls daemon readiness
- **THEN** the wizard reports the actual startup validation failure
- **AND** it does NOT report only `Daemon did not become ready` unless the failure
  reason is genuinely unavailable

### Requirement: Existing-install init menu

When `netclaw init` runs on an existing install, it SHALL open an action menu
with exactly these options:

- `Redo identity setup`
- `Open configuration editor`
- `Start over from scratch`
- `Cancel`

#### Scenario: Existing install opens action menu

- **GIVEN** `netclaw.json` exists
- **WHEN** the operator runs `netclaw init`
- **THEN** init opens the existing-install menu with the documented four
  options

#### Scenario: Existing install routes to config editor

- **GIVEN** the existing-install menu is open
- **WHEN** the operator chooses `Open configuration editor`
- **THEN** control routes to `netclaw config`

#### Scenario: Existing install routes to init-owned identity flow

- **GIVEN** the existing-install menu is open
- **WHEN** the operator chooses `Redo identity setup`
- **THEN** control routes to the init-owned identity flow

### Requirement: Start-over flow is double-confirmed

Choosing `Start over from scratch` SHALL open a second dialog with exactly:

- `Reset setup only`
- `Full reset`
- `Cancel`

Either destructive option SHALL require double confirmation before files are
mutated.

#### Scenario: Start-over dialog presents reset choices

- **GIVEN** the existing-install menu is open
- **WHEN** the operator chooses `Start over from scratch`
- **THEN** the second dialog presents `Reset setup only`, `Full reset`, and
  `Cancel`

#### Scenario: Destructive reset requires double confirmation

- **GIVEN** the operator selected either `Reset setup only` or `Full reset`
- **WHEN** the destructive flow proceeds
- **THEN** two distinct confirmations are required before mutation

### Requirement: No init-force flag in this flow

This bootstrap flow SHALL NOT rely on a `netclaw init --force` mode.
Existing-install reset behavior SHALL be owned by the in-TUI existing-install
menu and start-over dialogs.

#### Scenario: Existing-install reset does not require hidden flag

- **GIVEN** an existing install
- **WHEN** the operator wants to restart setup
- **THEN** the path is available from the existing-install init menu
- **AND** it does not depend on `netclaw init --force`

### Requirement: Init-owned editor re-entry uses existing config state

Init-owned editor re-entry on an existing install SHALL load existing config
into `WizardContext.ExistingConfig` and prefill non-secret values from that
state. Secret-bearing fields SHALL remain masked and empty.

#### Scenario: Provider re-entry keeps credential field masked

- **GIVEN** an existing provider configuration with stored credentials
- **WHEN** an init-owned provider flow re-enters
- **THEN** provider choice and non-secret fields are prefilled
- **AND** credential inputs remain blank with configured/not-set hint text

#### Scenario: Identity re-entry prefills init-owned fields

- **GIVEN** an existing install with agent name, operator name, and
  timezone already set
- **WHEN** an init-owned identity flow re-enters
- **THEN** those non-secret fields are prefilled

### Requirement: Init-owned writes use semantic merge

Init-owned editor flows SHALL write changes through semantic merge-on-save.
Unrelated config meaning and unrelated stored secrets SHALL be preserved even
if the serialized file text changes.

#### Scenario: Identity-only edit preserves unrelated config meaning

- **GIVEN** an existing install with configured channels, search, and
  exposure settings
- **WHEN** an init-owned identity flow updates only identity-owned data
- **THEN** the unrelated config sections remain semantically unchanged

#### Scenario: Blank secret submission preserves existing secret

- **GIVEN** an init-owned flow includes a secret-bearing field with an
  existing stored value
- **WHEN** the operator leaves that field blank and saves
- **THEN** the existing secret remains stored
- **AND** no decrypted value is shown in the UI
