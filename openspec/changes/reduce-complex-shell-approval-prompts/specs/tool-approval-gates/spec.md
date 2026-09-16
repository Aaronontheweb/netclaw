## ADDED Requirements

### Requirement: Complete Bash compounds retain scoped grant candidates

For a complete Bash command, Netclaw SHALL retain each possible executable occurrence as an approval candidate.
Netclaw SHALL apply every grant to the directory and path scope where that occurrence can execute.
If any reachable scope is unknown, Netclaw SHALL require exact approval or deny the call.
Netclaw SHALL keep hard denials, protected paths, redirects, audience limits, and one-time retry checks independent of grant coverage.

#### Scenario: A read pipeline uses grants for every reachable scope

- **GIVEN** the session starts in `/work` and has grants for `cd`, `cat`, `sed`, and `ls`
- **AND** grants cover the required paths in both `/work` and `/work/sub`
- **WHEN** the agent calls `cd /work/sub && cat result.txt | sed -n '1p'; ls .`
- **THEN** Netclaw can use reusable candidates for every executable occurrence and reachable path scope
- **AND** Netclaw permits execution only when every candidate has coverage

#### Scenario: A failed directory change retains the original scope

- **GIVEN** a folder grant covers `ls` in `/work/sub` but no grant covers `ls` in `/work`
- **WHEN** the agent calls `cd /work/sub && cat result.txt; ls .` from `/work`
- **THEN** Netclaw does not treat the `ls` grant for `/work/sub` as coverage for `/work`
- **AND** Netclaw requests approval or denies the call before execution

#### Scenario: An ungranted verb stays subject to approval

- **GIVEN** grants cover `cd` and `cat` in each reachable scope
- **WHEN** a complete command also calls `python3` without a matching grant
- **THEN** Netclaw requests approval or denies the `python3` occurrence before execution

#### Scenario: An unknown path stays exact

- **GIVEN** a parent folder grant covers `/work`
- **WHEN** a command refers to `/work/*/result.txt` and a match can cross a symbolic link
- **THEN** Netclaw does not infer that the grant covers every possible target
- **AND** Netclaw requires exact approval or denies the call

### Requirement: Directory advice keeps the original command inert

Netclaw SHALL offer a typed one-call directory correction when an eligible call uses an exact leading Bash directory change for ordinary project work.
The correction SHALL identify the intended `WorkingDirectory` and SHALL not rewrite the command, execute a process, or create a grant.
Netclaw SHALL evaluate a replacement call through the normal shell policy.
Netclaw SHALL suppress this advice when the target is unsafe or unresolved.

#### Scenario: An eligible project read receives one-call advice

- **GIVEN** the session project is `/work` and `/work/sub` is an allowed directory
- **WHEN** the agent calls `cd /work/sub && cat result.txt` for a file read
- **THEN** Netclaw can suggest `WorkingDirectory=/work/sub` for a replacement call
- **AND** Netclaw does not execute the original command

#### Scenario: A requested directory mutation keeps its meaning

- **GIVEN** the user asks for shell directory behavior
- **WHEN** the agent calls `cd /work/sub && pwd`
- **THEN** Netclaw does not silently replace or execute a different command
- **AND** the original call retains normal approval policy
