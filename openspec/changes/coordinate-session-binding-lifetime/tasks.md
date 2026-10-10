## 1. Baseline and race proof

- [x] 1.1 Record the session-owned lifecycle prototype in [PR #2429](https://github.com/netclaw-dev/netclaw/pull/2429) and verify its two focused Slack and Discord passivation tests pass.
- [x] 1.2 Add a deterministic input-during-drain test and verify it exposes the race before the parent retirement barrier fix.

## 2. Graceful channel binding retirement

- [x] 2.1 Add the parent retirement notice and FIFO barrier for Slack, Discord, and Mattermost; verify each binding drains before the parent stops it.
- [x] 2.2 Preserve parent-queued and returned mailbox deliveries with original payloads and reply targets; verify the replacement binding receives each delivery once and in order.
- [x] 2.3 Cover input that the old binding receives after deactivation; verify it returns the input to the parent without writing to the drained pipeline.
- [x] 2.4 Keep the parent ingress ACL and routing checks before queue admission; verify denied input during retirement never reaches the replacement pipeline.
- [x] 2.5 Keep the parent alive while it has binding children; verify it stops only after all children terminate.

## 3. Session idle policy

- [ ] 3.1 Set the existing idle timeout default to one hour and use actor-local active-work state; verify active jobs block idle passivation and reaped job records do not.
- [x] 3.2 Keep subscriber count out of idle eligibility and retain journaled approval recovery; verify the existing approval passivation and response tests pass.
- [ ] 3.3 Keep active `Processing` phases outside idle passivation; verify current turn completion still resets the idle timer.

## 4. Contract reconciliation and validation

- [ ] 4.1 After the race tests pass, update the five old-contract test expectations for active-job reaping, subscriber veto, and the idle-timeout default.
- [ ] 4.2 Reconcile the main OpenSpec capabilities and the mapped operational skill with the approved behavior; verify strict OpenSpec validation and the required repository checks.
- [ ] 4.3 Run the full test feed and verify the new channel race tests, approval recovery tests, and session lifetime contracts pass.
- [x] 4.4 Track local pipeline-queue admission as a separate follow-up; do not claim durable session admission or add an acknowledgement or flush protocol in this change.
