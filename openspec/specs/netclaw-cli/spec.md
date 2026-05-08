## Purpose

Define operator-facing CLI surface area for Netclaw: the `netclaw init` wizard,
the `netclaw doctor` diagnostic, and the `netclaw approvals` command for
managing persistent tool approvals.

## Requirements

### Requirement: Init wizard approval mode selection

The `netclaw init` wizard SHALL ask about shell approval mode when configuring
each audience profile that has shell access enabled. The wizard SHALL present
three options: Approval (recommended default), Unrestricted (HostAllowed with
no approval), and Off (shell disabled). The selected mode SHALL be written to
the audience profile's `ApprovalPolicy` in `netclaw.json`. For Personal,
selecting Approval SHALL explicitly write
`Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute = "approval"`
rather than relying on runtime audience defaults.

#### Scenario: Init wizard prompts for Personal shell mode

- **GIVEN** the user is running `netclaw init`
- **WHEN** the wizard configures the Personal audience profile
- **AND** shell mode is not Off
- **THEN** the wizard asks: "Shell approval mode for Personal?"
- **AND** offers Approval (default), Unrestricted, and Off

#### Scenario: Init wizard skips approval for audiences with shell off

- **GIVEN** the user is running `netclaw init`
- **WHEN** the wizard configures an audience with shell mode Off
- **THEN** the wizard does NOT ask about approval mode for that audience

#### Scenario: Selection written to config

- **GIVEN** the user selects "Approval" for Personal audience
- **WHEN** the wizard writes the config
- **THEN** `netclaw.json` includes
  `Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute = "approval"`

### Requirement: Doctor checks for approval configuration

`netclaw doctor` SHALL validate approval configuration consistency. It SHALL
warn when the Personal audience enables host shell access without an explicit
`shell_execute` approval gate in `ApprovalPolicy.ToolOverrides`. It SHALL warn
when `tool-approvals.json` contains patterns for audiences or tools that are no
longer configured.

#### Scenario: Doctor warns about Personal host shell without explicit approval gate

- **GIVEN** the Personal audience has host shell access enabled
- **AND** `ApprovalPolicy.ToolOverrides` does not contain `shell_execute`
- **WHEN** `netclaw doctor` runs
- **THEN** it emits a warning that Personal host shell is enabled without an
  explicit `shell_execute` approval gate
- **AND** the warning recommends running `netclaw init` again or setting
  `Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute = "approval"`

#### Scenario: Doctor warns about stale approval patterns

- **GIVEN** `tool-approvals.json` has patterns for `team.shell_execute`
- **AND** the Team audience has shell mode Off
- **WHEN** `netclaw doctor` runs
- **THEN** it emits an info advisory: "Persistent approvals exist for
  team.shell_execute but shell is disabled for Team audience."

### Requirement: Operator CLI for persistent tool approvals

The CLI SHALL provide a `netclaw approvals` command surface for inspecting
and revoking entries in the persistent approvals file
(`~/.netclaw/config/tool-approvals.json`). The command SHALL operate on the
file directly via `Netclaw.Configuration.ToolApprovalStore` without
requiring the daemon to be running. Bare `netclaw approvals` (and
`netclaw approvals tui`) SHALL launch an interactive Termina TUI page.
Single-shot subcommands SHALL be `list`, `revoke`, and `help`.

`list` SHALL accept `--audience <personal|team|public>`, `--tool <name>`,
and `--json`. Without flags it SHALL print every audience and tool group
in a stable order.

`revoke <pattern>` SHALL remove only entries that match `<pattern>` exactly
under the same case-sensitivity rules that the daemon uses for shell
approval matching (Ordinal on POSIX, OrdinalIgnoreCase on Windows).
`revoke` SHALL accept `--audience` and `--tool` to scope the removal.
`revoke --tool <name> --all` SHALL clear every entry for that tool in the
targeted audiences. `revoke` of a pattern that does not match any entry
SHALL exit non-zero with a clear message; the CLI SHALL NOT silently
succeed.

The CLI SHALL NOT add or upgrade approvals; it is read-and-revoke only.
Exit codes SHALL be 0 for success and 1 for user errors (bad flag combos,
unknown audience, no match for revoke, `--all` without `--tool`). When the
underlying store has quarantined a malformed file (`tool-approvals.json.invalid`
sibling), the CLI SHALL emit a warning before list/revoke output and
SHALL NOT silently swallow the condition.

#### Scenario: Empty approvals file lists no entries with exit zero

- **GIVEN** `tool-approvals.json` does not exist or contains `{}`
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the CLI prints `No persistent approvals.`
- **AND** exits with code `0`

#### Scenario: List filters by audience

- **GIVEN** `tool-approvals.json` contains entries under `personal` and `team`
- **WHEN** the operator runs `netclaw approvals list --audience personal`
- **THEN** only the `personal` audience entries are printed

#### Scenario: List emits JSON with audience/tool/pattern shape

- **GIVEN** `tool-approvals.json` contains
  `{"audiences":{"personal":{"shell_execute":["git push"]}}}`
- **WHEN** the operator runs `netclaw approvals list --json`
- **THEN** the output is valid JSON
- **AND** the structure groups patterns by audience and tool

#### Scenario: Revoke removes only exact matches

- **GIVEN** `tool-approvals.json` contains
  `{"audiences":{"personal":{"shell_execute":["git push","/home/.netclaw/logs/"]}}}`
- **WHEN** the operator runs `netclaw approvals revoke "git push" --tool shell_execute --audience personal`
- **THEN** the `git push` entry is removed
- **AND** `/home/.netclaw/logs/` remains
- **AND** the CLI exits with code `0`

#### Scenario: Revoke with no match exits non-zero

- **GIVEN** `tool-approvals.json` does not contain `git push`
- **WHEN** the operator runs `netclaw approvals revoke "git push"`
- **THEN** the CLI prints a no-match message
- **AND** exits with code `1`
- **AND** does not modify the file

#### Scenario: Revoke --tool --all clears all entries for the tool

- **GIVEN** `tool-approvals.json` contains multiple `shell_execute` entries
  under `personal`
- **WHEN** the operator runs `netclaw approvals revoke --tool shell_execute --audience personal --all`
- **THEN** every `shell_execute` entry under `personal` is removed
- **AND** entries for other tools and other audiences are untouched

#### Scenario: Revoke --all without --tool is rejected

- **WHEN** the operator runs `netclaw approvals revoke --all`
- **THEN** the CLI rejects the invocation with a clear usage message
- **AND** exits with code `1`
- **AND** does not modify the file

#### Scenario: Daemon picks up CLI-applied revocation without restart

- **GIVEN** the daemon is running and has previously approved `git push`
- **WHEN** the operator runs `netclaw approvals revoke "git push" --tool shell_execute --audience personal`
- **AND** a new session attempts `git push` afterwards
- **THEN** the daemon prompts for approval again
- **AND** the daemon was not restarted

#### Scenario: Bare invocation launches the TUI

- **WHEN** the operator runs `netclaw approvals` with no subcommand
- **THEN** the CLI launches the interactive Termina approvals page
