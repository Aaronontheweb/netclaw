## 1. Session idle policy

- [x] 1.1 Keep the existing session idle timer, set its default to one hour, and defer idle passivation for active work.
- [x] 1.2 Keep subscriber count out of idle eligibility and preserve journaled approval recovery.
- [ ] 1.3 Verify active shell-job state blocks passivation, reaped records do not, and `Processing` still disables the idle timeout.

## 2. Channel binding lifetime

- [x] 2.1 Remove independent idle stops from Slack, Discord, and Mattermost bindings.
- [x] 2.2 Keep each binding's session output subscription until committed `SessionDeactivated`; drain its pipeline and then stop.
- [x] 2.3 Keep conversation parents alive while binding children exist. Verify each parent stops after its last binding child terminates.
- [x] 2.4 Verify the Slack and Discord lifecycle tests and the three approval-recovery tests pass. Do not claim Mattermost lifecycle proof.

## 3. Contract reconciliation and validation

- [ ] 3.1 Update the old-contract tests for active-job reaping, subscriber veto, and the one-hour default.
- [ ] 3.2 Reconcile the main OpenSpec capabilities and the mapped operational skill with this lifetime behavior.
- [ ] 3.3 Run strict OpenSpec validation, required repository checks, and the full test feed.

Input replay during pipeline drain, parent retirement barriers, and delivery guarantees remain out of scope.
