## 1. Protocol messages

- [ ] 1.1 Add `ApprovalResponseReceived` message type in `Netclaw.Actors.Protocol` carrying `SessionId`, `CallId`, `OptionKey`, `ApprovingSenderId`, `Channel`, `MessageTs`, and `ResponseUrl` (Slack-specific fields nullable for Discord)
- [ ] 1.2 Add `RenderResolvedApproval` message type in `Netclaw.Actors.Protocol` carrying `Channel`, `MessageTs` (or Discord message identifier), `ToolInteractionRequest`, `SelectedKey`, and `ApprovingSenderId`
- [ ] 1.3 Wire both new messages through the existing serialization registration so they round-trip via the configured serializer (acceptance: serializer round-trip unit test passes)

## 2. Session actor inbound handler

- [ ] 2.1 Add a handler on `LlmSessionActor` for `ApprovalResponseReceived` that runs `ApprovalButtonValueCodec.CanApprove(requesterPrincipal, requesterSenderId, approvingSenderId)` against the actor-owned pending-call state
- [ ] 2.2 Implement prefix-match of incoming `CallId` against the session's pending-call set, scoped to a single session (acceptance: matches under truncated callId per `MaxEncodedLength = 100`)
- [ ] 2.3 On match + authorized: produce the equivalent `ToolInteractionResponse` the binding actor would have produced today and resolve the pending tool call
- [ ] 2.4 On match + unauthorized (`CanApprove` returns false): log + drop, leave pending call unresolved (acceptance: existing CanApprove unit test parity)
- [ ] 2.5 On no-match (unknown `CallId`): log + drop, do NOT crash (acceptance: feeding a synthetic unknown callId leaves session state unchanged)
- [ ] 2.6 After resolving, emit `RenderResolvedApproval` to the corresponding output binding for the redraw

## 3. Slack ingress

- [ ] 3.1 In `SlackConversationActor`, on `block_actions` payload with an approval `action_id`, decode the payload and `ApprovalButtonValueCodec.TryDecode` the button value to extract `callId`, `optionKey`, `requesterSenderId`
- [ ] 3.2 Resolve `SessionId` from `(channel.id, message.thread_ts)` and address `ApprovalResponseReceived` to `session-manager/{persistenceId}` via `Tell` — do NOT call `Context.Child(threadName)` for routing
- [ ] 3.3 Remove the `Ignoring Slack approval response for missing thread` drop path for button responses (text-reply path keeps its existing routing through `SlackThreadBindingActor`)
- [ ] 3.4 In `SlackThreadBindingActor`, drop reliance on `_pendingApprovalRequests` for **button** routing only; retain the field for text-reply matching and passivation-deferral hint
- [ ] 3.5 Implement `SlackThreadBindingActor` handler for `RenderResolvedApproval` that calls the existing pure `BuildResolvedApprovalBlocks(...)` and posts via `chat.update` (or `response_url` `replace_original` if `chat.update` fails) — handler must work with empty `_pendingApprovalRequests`

## 4. Discord ingress

- [ ] 4.1 In `DiscordConversationActor`, on interaction payload with an approval `custom_id`, decode and route to the session actor via `ApprovalResponseReceived` — symmetric to Slack
- [ ] 4.2 Remove the equivalent `IsNobody()` drop path for button responses
- [ ] 4.3 In `DiscordSessionBindingActor`, drop reliance on `_pendingApprovalRequests` for button routing only; retain for text-fallback and passivation-deferral
- [ ] 4.4 Implement `DiscordSessionBindingActor` handler for `RenderResolvedApproval` that produces the resolved Discord message from the pure builder

## 5. Tests

- [ ] 5.1 Unit test: `LlmSessionActor` resolves `ApprovalResponseReceived` for a pending callId and emits the matching `ToolInteractionResponse` (one test per `OptionKey`: `ApprovedOnce`, `ApprovedSession`, `ApprovedAlways`, `Denied`)
- [ ] 5.2 Unit test: `LlmSessionActor` rejects `ApprovalResponseReceived` when `CanApprove` returns false and leaves the pending call unresolved
- [ ] 5.3 Unit test: `LlmSessionActor` ignores `ApprovalResponseReceived` for an unknown callId without crashing
- [ ] 5.4 Unit test: prefix-match against truncated callId resolves to the full pending call when only one prefix matches
- [ ] 5.5 Integration test (Slack): button click delivered when channel-level conversation actor has been stopped — response reaches the session actor and resolves the pending call (regression test for the production incident in #979)
- [ ] 5.6 Integration test (Slack): text-reply path continues to route through `SlackThreadBindingActor` unchanged (regression guard for the existing `netclaw-slack-socket` text-reply requirement)
- [ ] 5.7 Integration test (Discord): button click delivered when `DiscordSessionBindingActor` has stopped — symmetric to 5.5
- [ ] 5.8 Integration test (Discord): text-fallback path unchanged
- [ ] 5.9 Test: `RenderResolvedApproval` arriving at a freshly-spawned binding (no prior `_pendingApprovalRequests`) produces the resolved-state blocks correctly

## 6. Quality gates

- [ ] 6.1 `dotnet slopwatch analyze` passes with no new violations
- [ ] 6.2 `./scripts/Add-FileHeaders.ps1 -Verify` passes for all new `.cs` files
- [ ] 6.3 `openspec validate route-button-approvals-by-session-id` continues to pass
- [ ] 6.4 No new `Thread.Sleep` / `Task.Delay` in test orchestration (use `AwaitAssertAsync` or proper synchronization signals per `CLAUDE.md` testing guidelines)

## 7. Operational verification

- [ ] 7.1 Manual smoke: start daemon, post an approval prompt in Slack, force passivation of the channel-level adapter (idle for the configured timer or via test hook), click Approve — verify the response is delivered to the session actor and the message is rewritten to the resolved state
- [ ] 7.2 Manual smoke: same flow in Discord
- [ ] 7.3 Verify the daemon log no longer emits `Ignoring Slack approval response for missing thread` (or its Discord equivalent) under the smoke scenario above
- [ ] 7.4 Run the eval suite (`./evals/run-evals.sh`) — even though the change is in routing, the approval flow is integration-adjacent enough to warrant a pass

## 8. Sync and archive

- [ ] 8.1 `/opsx-verify` against the change to confirm implementation matches the artifacts
- [ ] 8.2 `/opsx-sync` to apply spec deltas to `openspec/specs/`
- [ ] 8.3 `/opsx-archive` once the PR is merged
- [ ] 8.4 Add a comment to upstream issue #939 noting that this change has landed and that #939's scope can narrow to persistence-only (Phase 2 "resurrect binding" no longer needed)
