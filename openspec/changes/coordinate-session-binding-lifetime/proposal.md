## Why

A channel binding can stop while its session still owns work. The session then loses its channel output path. During binding retirement, the parent can also hold input that the old binding has not submitted to its pipeline. This change gives the session, binding, and conversation parent one coordinated lifetime for those queued deliveries.

This work supports PRD-001 FR-001, FR-002, and FR-003, and PRD-009. It keeps thread identity, output delivery, and recovery aligned.

## What Changes

- Set the existing session idle timeout to one hour. Keep the current timer and use actor-local work state to defer idle stop.
- Let active session work defer idle stop. Use the existing shell-job state. Ignore shell-job entries with `ReapedAtMs` set. Keep the current `Processing` timeout guard.
- Do not let subscriber count defer session idle stop. Keep journaled approval passivation and recovery unchanged.
- Emit `SessionDeactivated` only after the session commits to stop. Each binding drains its pipeline, then it retires through its conversation parent.
- Buffer authorized deliveries during binding retirement. Preserve each payload, its original `ReplyTo`, and FIFO order. Recreate the binding after `Terminated` when a delivery or an authorized ensure request waits.
- Let a conversation parent idle-stop only when it has no binding children. A channel binding has no independent idle timeout.
- Keep ingress ACL and routing checks in the conversation parent. Keep per-command authorization checks, output rendering, approval checks, and session identity in their current owners.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-state-machine`: idle passivation uses the one-hour timeout and active-work guard. Subscriber count does not block passivation.
- `session-resume`: live subscribers do not veto passivation. Journaled approvals still passivate and recover through the current response route.
- `channel-binding-parity`: Slack, Discord, and Mattermost bindings retire through a FIFO parent barrier after they drain their pipelines.
- `background-job-execution`: active shell jobs block idle passivation. Reaped job records do not block it.

## Impact

The change affects session passivation, channel binding lifecycle, and conversation child cleanup. It adds no gateway output route, durable outbox, crash-delivery guarantee, new timer, or activity classifier.

### Security impact

Conversation parents keep their current ingress ACL and routing checks. Bindings keep their per-command authorization checks. Replayed deliveries preserve their original message and reply target. This change does not promise session admission for input that the binding already wrote to its local pipeline queue.

### Operational impact

A graceful stop may buffer deliveries until the replacement binding starts. Input already submitted to the local pipeline queue has no durable admission acknowledgement. Actor or process failure remains outside this contract.

PR #2429 contains the current session-owned lifecycle prototype. Two focused Slack and Discord passivation tests passed. The drain-race RED proof now fails as expected in Slack and Discord. Broader tests still expose five old-contract expectations that this change must reconcile after the race fix.
