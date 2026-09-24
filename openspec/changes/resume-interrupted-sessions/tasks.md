## 1. Durable input

- [x] 1.1 Persist accepted input before acknowledgment.
- [x] 1.2 Store pending input and recent source IDs in snapshots.
- [x] 1.3 Close input from completed, failed, and tool started turns.
- [x] 1.4 Verify journal, snapshot, order, and source retry behavior.

## 2. Graceful drain

- [ ] 2.1 Retain and cancel the active model task after a short grace.
- [ ] 2.2 Return one standard reminder definition for eligible pending input.
- [ ] 2.3 Exclude approvals, tool work, partial replies, and unsupported channels.

## 3. Existing reminder path

- [ ] 3.1 Store reminder definitions in the restart manifest.
- [ ] 3.2 Register fresh reminders through the reminder manager after startup.
- [ ] 3.3 Restore pending input under its original context when the reminder arrives.
- [ ] 3.4 Verify expiration, duplicate registration, and a cold session wakeup.

## 4. Verification

- [ ] 4.1 Update SPEC-011 and the operations skill.
- [ ] 4.2 Run actor and daemon tests, evals, Slopwatch, headers, and OpenSpec validation.
