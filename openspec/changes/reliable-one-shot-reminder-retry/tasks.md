## 1. Dependency and persistence

- [x] 1.1 Upgrade both Akka.Reminders packages to version 0.7.0 and configure the 70-minute acknowledgement timeout.
- [x] 1.2 Add backward-compatible terminal outcome and consecutive failure fields to reminder definitions.
- [x] 1.3 Add old-shape load and round-trip tests for reminder JSON files.

## 2. Execution and retry

- [x] 2.1 Pass the durable envelope to every reminder execution mode.
- [x] 2.2 Acknowledge success and negatively acknowledge known execution or delivery failures.
- [x] 2.3 Add the one-hour absolute attempt limit and session-termination failure detection.
- [x] 2.4 Use the occurrence due time for stable retry session identity.

## 3. Lifecycle and reconciliation

- [x] 3.1 Persist each reminder-level failure increment and each success reset.
- [x] 3.2 Retain retryable one-shots and soft-delete completed or terminal one-shots.
- [x] 3.3 Remove automatic hard deletion from reconciliation and use durable occurrence status.
- [x] 3.4 Expose occurrence retry and terminal details through reminder status.

## 4. Proof and guidance

- [x] 4.1 Add actor tests for retry, later success, poison pause, restart state, and reconciliation retention.
- [x] 4.2 Update the `netclaw-operations` system skill and its version.
- [x] 4.3 Run focused tests, the full affected suites, evals, Slopwatch, and file-header verification.
