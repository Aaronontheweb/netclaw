## 1. Slack history fetcher

- [x] 1.1 Add a root-only predicate in
  `src/Netclaw.Channels.Slack/SlackThreadHistoryFetcher.cs`:
  bot-authored entries (`bot_id` present) are dropped unless
  `message.Ts == threadTs.Value`.
- [x] 1.2 Retain the sender-id derivation (user → bot id → drop).
- [x] 1.3 Inline comment references issue #955 and notes the watermark
  is a cost optimization, not a correctness primitive.

## 2. Slack history fetcher unit tests

- [x] 2.1 `Includes_bot_authored_root_for_proactive_post_bootstrap`:
  bot at root hydrated.
- [x] 2.2 `Excludes_bot_authored_replies_below_thread_root`: regression
  test for issue #955.
- [x] 2.3 `Excludes_bot_below_root_even_when_root_is_also_bot`:
  pathological mix.
- [x] 2.4 `Bot_post_with_only_bot_id_is_included_at_root`: sender id
  fallback at root.
- [x] 2.5 `Prefers_user_id_over_bot_id_when_both_are_present`:
  sender id preference at root.

## 3. Discord history fetcher

- [x] 3.1 Add a root-only predicate in
  `src/Netclaw.Channels.Discord/Transport/DiscordThreadHistoryFetcher.cs`:
  bot-authored entries (`IsBot == true`) are dropped unless
  `MessageId == threadChannelId.ToString()`.
- [x] 3.2 Inline comment references issue #955 and explains the
  Discord thread-channel-id-equals-root-message-id convention.
- [x] 3.3 Add `using System.Globalization;` for invariant string
  formatting of the threadChannelId comparison key.

## 4. Discord history fetcher unit tests

- [x] 4.1 `Includes_bot_authored_root_for_proactive_post_bootstrap`:
  Discord parallel of 2.1.
- [x] 4.2 `Excludes_bot_authored_replies_below_thread_root`: Discord
  parallel of 2.2.
- [x] 4.3 `Excludes_bot_below_root_even_when_root_is_also_bot`:
  Discord parallel of 2.3.

## 5. End-to-end Slack integration regression tests

- [x] 5.1
  `SlackThreadBackfillIntegrationTests.Bot_replies_below_thread_root_are_excluded_from_adopted_context`:
  end-to-end assertion that mid-thread bot replies do NOT appear in
  the merged user turn's adopted-context window. This is the test
  that would have caught issue #955.
- [x] 5.2
  `SlackThreadBackfillIntegrationTests.Bot_authored_thread_root_is_hydrated_for_proactive_post_bootstrap`:
  end-to-end assertion that the bot's root IS hydrated.

## 6. Spec delta finalization

- [x] 6.1 Rewrite
  `openspec/changes/add-proactive-channel-sessions-conformance/specs/thread-history-backfill/spec.md`
  with the root-only requirement and scenarios covering root inclusion,
  below-root exclusion, mixed cases, sender id derivation, and the
  Discord DM limitation.
- [x] 6.2 Update proposal.md and design.md to reflect the corrected
  framing (root-only predicate; watermark as cost optimization, not
  correctness primitive).
- [x] 6.3 Run `openspec validate add-proactive-channel-sessions-conformance`
  to confirm artifacts validate.

## 7. Archive

- [x] 7.1 Verify implementation matches the corrected spec.
- [x] 7.2 Archive the change so the updated requirement lands in
  `openspec/specs/thread-history-backfill/spec.md`.

## 8. Quality gates

- [x] 8.1 `dotnet test --filter
  FullyQualifiedName~SlackThreadBackfill|FullyQualifiedName~ThreadHistoryFetcher`:
  passes for both Slack and Discord (30 tests).
- [x] 8.2 `dotnet slopwatch analyze`: clean (0 issues).
- [x] 8.3 `pwsh scripts/Add-FileHeaders.ps1 -Verify`: clean (all files
  have headers).
