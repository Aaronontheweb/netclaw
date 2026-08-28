# Session Storage Runbook

Use this runbook to inspect session files after an upgrade.
See the [engineering glossary](../spec/GLOSSARY.md#filesystem-and-output-terms) for the shared terms.

## Storage Layouts

Netclaw gives each new session one durable version-2 storage binding.
The binding stores the absolute [session storage envelope](../spec/GLOSSARY.md#session-storage-envelope).
The session workspace is the envelope's `workspace/` directory.
The complete envelope is not a workspace or an authority grant.

Existing sessions have no binding.
They keep their established workspace and log paths.
Netclaw does not move, copy, or rename their files.

Example: A configuration change does not change an existing version-2 binding.

Counterexample: Netclaw does not recompute only the log path from the new configuration.

## Parent And Child Paths

The `[session]` context gives a private run these exact paths:

- `session_dir` is the workspace and default relative-path base.
- `temp_dir` contains disposable files for the current run.
- `artifact_dir` contains outputs that the parent or user must keep.
- `log_path` is the raw log for the current run.

Netclaw sets `TMPDIR`, `TMP`, and `TEMP` for each child process.
Standard native and .NET temporary APIs therefore select `temp_dir`.
Netclaw does not change the daemon process environment.

Example: A child writes a diagnostic archive below its own `temp_dir`.

Counterexample: A child does not treat `session_dir` as disposable storage.

## Log Access

Existing file tools can read, list, and search same-session logs.
This scope includes parent and child logs for the same session.
It does not grant file changes, attachments, or shell authority.
It does not grant access to another session.

A successful `spawn_agent` result supplies the exact child log path.
Use that path with an existing file tool.
Do not search the global log directory with a shell command.

Example: `file_read` reads the returned child `LogPath` while the child writer remains open.

Counterexample: `file_write` cannot change the same log.

## Managed Worktrees

Use `worktree_create` for a new Git worktree.
Supply a branch and an authorized source repository.
Do not supply a destination.
Netclaw selects a collision-safe directory below the session worktree area.

The tool changes project scope only after Git succeeds.
The tool records session and run ownership.
Netclaw does not delete the worktree when the session ends.

Example: A successful call returns the canonical worktree path.

Counterexample: A failed Git call does not change project scope.

## Upgrade Checks

Use these checks after an upgrade:

1. Resume one existing session and confirm its workspace and log paths stay unchanged.
2. Start one new session and record its `session_dir` and `log_path`.
3. Restart the daemon and confirm the new paths stay unchanged.
4. Start one child and read its returned log path with `file_read`.
5. Confirm a different session cannot read that log.

Warning: A pre-feature binary cannot resume a session that has a version-2 binding.
This downgrade path is outside the supported scope.
