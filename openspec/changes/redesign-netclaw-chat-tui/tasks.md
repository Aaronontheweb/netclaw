## 1. Planning and Issue Traceability

- [x] 1.1 Update `PRD-004` with the inline chat, structured event, input, copy, and approval requirements.
- [x] 1.2 Update `PRD-009` with the complete typed output and structured resume contract.
- [x] 1.3 Replace the old chat section in `TUI-001` with the approved named-region mockups and responsive rules.
- [x] 1.4 Update `SPEC-004` with the chat command, session picker boundary, and explicit presentation modes.
- [x] 1.5 Update `SPEC-002` and `SPEC-011` with output correlation, transport mapping, and structured resume behavior.
- [x] 1.6 Update `SPEC-010` with the headless, compatibility, and native terminal proof matrix.
- [x] 1.7 Update the current Netclaw GitHub issues `#577` and `#1338` with the approved scope and OpenSpec link.
- [x] 1.8 File the remaining Netclaw epic and child issues without duplicates, then add their links to this change.
- [x] 1.9 Update the current Termina GitHub issues `#45` and `#240` with the applicable scope and design link.
- [x] 1.10 File the remaining Termina epic and prototype issues without duplicates, then add their links to this change.

## 2. Termina Extend-Only Compatibility Foundation

- [x] 2.1 Add a public API approval baseline for the current Termina release.
- [x] 2.2 Add `TerminalPresentationMode` with stable explicit numeric values.
- [x] 2.3 Add `TerminaRuntimeOptions.PresentationMode` with `FullScreen` as the default.
- [x] 2.4 Append `NativeTerminal` to `ScrollInputMode` without a change to current values.
- [x] 2.5 Add `IInlineTerminalControl` without a change to `IAnsiTerminal`.
- [x] 2.6 Implement `IInlineTerminalControl` in `AnsiTerminal` and `VirtualTerminal`.
- [x] 2.7 Make `TerminaApplication` enter the alternate buffer only in `FullScreen` mode.
- [x] 2.8 Preserve direct `AnsiTerminal(bool)` behavior and make application dependency injection own buffer selection.
- [x] 2.9 Add full-screen regression tests for startup, render, resize, and exit behavior.
- [x] 2.10 Verify the approved API diff contains additive public changes only.

## 3. Termina Inline Coordinator Prototype

- [x] 3.1 Add an inline coordinator that owns a bounded live region in the primary buffer.
- [x] 3.2 Add the ordered erase, stable commit, and live redraw sequence.
- [x] 3.3 Add an additive `IInlineOutput` service for stable layout commits.
- [ ] 3.4 Route internal diagnostics through the inline output owner.
- [x] 3.5 Add `VirtualTerminal` tests for one stable commit and one live redraw.
- [x] 3.6 Add `VirtualTerminal` tests for parallel commits and deterministic output order.
- [x] 3.7 Add resize tests for narrower, wider, and wide-character live content.
- [x] 3.8 Add failure tests that verify cursor and terminal-mode recovery.
- [ ] 3.9 Run the prototype on Linux terminals and tmux, then record the exact evidence.
- [ ] 3.10 Run the prototype on macOS and Windows Terminal, then record the exact evidence.
- [ ] 3.11 Accept inline mode only if resize, scrollback, selection, paste, and exit recovery pass the matrix.

## 4. Termina Input, Scroll, and Copy Primitives

- [ ] 4.1 Add a text-history cancellation API that restores the saved draft.
- [ ] 4.2 Add typed-key tests for Up, Down, draft restoration, and history cancellation.
- [ ] 4.3 Verify `Shift+Enter` across legacy, Kitty, and native raw input paths.
- [ ] 4.4 Add a visible capability result when a terminal cannot distinguish `Shift+Enter`.
- [ ] 4.5 Add dimension-free scroll operations that use the measured viewport.
- [ ] 4.6 Preserve mouse coordinates on wheel input and test route selection.
- [ ] 4.7 Add semantic copy data that remains separate from display glyphs.
- [ ] 4.8 Add clipboard failure output that preserves the selected semantic data.
- [ ] 4.9 Add headless tests that exclude borders, control bytes, and truncated display text from copied data.

## 5. Netclaw Session Output Contract

- [ ] 5.1 Add `ToolActivityOutput` with `CallId`, turn identity, safe phase, and safe summary.
- [ ] 5.2 Relay current nonterminal tool activity through the session actor output boundary.
- [ ] 5.3 Add additive `RunId` and parent `CallId` fields to `SubAgentOutput`.
- [ ] 5.4 Populate stable sub-agent identities for start, activity, and completion events.
- [ ] 5.5 Add nullable wire fields and a discriminator for every new output value.
- [ ] 5.6 Map every current compaction, error, usage, file, turn, tool, and sub-agent field in both directions.
- [ ] 5.7 Apply `OutputFilter.ToolCalls` to tool activity and sub-agent activity.
- [ ] 5.8 Prove that Slack and other restricted subscribers do not receive the new activity.
- [ ] 5.9 Prove that transient activity does not enter model context or the actor journal.
- [ ] 5.10 Add DTO round-trip and old-payload fixtures for all additive fields.

## 6. Structured Session Resume

- [ ] 6.1 Add a framework-owned settled transcript entry union with stable discriminators.
- [ ] 6.2 Add nullable `RecentTranscript` properties without a change to `RecentMessages`.
- [ ] 6.3 Add a bounded settled timeline to session state and snapshots with new serialization tags.
- [ ] 6.4 Add settled transcript entries to `TurnRecorded` with new serialization tags.
- [ ] 6.5 Build settled entries from user, assistant, tool, sub-agent, file, error, usage, and compaction events.
- [ ] 6.6 Add read support for old journals and snapshots before new timeline writes start.
- [ ] 6.7 Convert supported `SerializableChatMessage` history to explicit legacy transcript entries.
- [ ] 6.8 Emit a diagnostic entry for unsupported legacy detail without a false active state.
- [ ] 6.9 Emit both `RecentMessages` and `RecentTranscript` during the compatibility period.
- [ ] 6.10 Add journal, snapshot, SignalR, and client resume fixtures across old and new shapes.

## 7. Netclaw Presentation Reducer and Visual Grammar

- [ ] 7.1 Add immutable chat presentation state with keys for turns, tool calls, sub-agents, thoughts, and approvals.
- [ ] 7.2 Add a pure reducer that maps every `SessionOutput` to state and explicit effects.
- [ ] 7.3 Add parallel tool tests where results finish in a different order than calls.
- [ ] 7.4 Add parallel same-name sub-agent tests that prove stable row identity.
- [ ] 7.5 Add the `Session Header`, `Transcript`, `Activity Rail`, `Decision Gate`, `Composer`, and `Status Line` regions.
- [ ] 7.6 Add borderless settled user, assistant, tool, thought, sub-agent, file, error, usage, and compaction forms.
- [ ] 7.7 Add concise live forms and immutable settled forms for each event lifecycle.
- [ ] 7.8 Add responsive layout rules and snapshots at 40, 60, 80, and 120 columns.
- [ ] 7.9 Add tail-follow state, a new-event count, and an explicit return-to-tail action.
- [ ] 7.10 Replace fixed scroll dimensions with the actual measured viewport.
- [ ] 7.11 Route all chat output through the inline output owner.
- [ ] 7.12 Add a visible diagnostic for an unsupported output type or invalid lifecycle transition.

## 8. Composer, Approval, Inspector, and Copy Behavior

- [ ] 8.1 Configure `Shift+Enter` for a newline and bare `Enter` for submission.
- [ ] 8.2 Restore the saved draft after prompt history reaches its newest entry.
- [ ] 8.3 Add double Escape with an injected `TimeProvider` and a defined interval.
- [ ] 8.4 Give a pending approval priority over composer Escape behavior.
- [ ] 8.5 Block paste delivery to a hidden composer while an approval owns focus.
- [ ] 8.6 Preserve compact and expanded approval forms with `Ctrl+O`.
- [ ] 8.7 Preserve the approval selection and bounded detail position across `Ctrl+O` changes.
- [ ] 8.8 Render approval control characters as visible safe text.
- [ ] 8.9 Add an inspector that shows complete event detail without transcript truncation.
- [ ] 8.10 Queue inline output while the inspector owns the terminal and commit it after exit.
- [ ] 8.11 Add semantic copy for an event and a complete turn.
- [ ] 8.12 Add visible copy errors and keep the selected data after a failure.

## 9. Netclaw Command Integration

- [ ] 9.1 Configure `netclaw chat` for explicit `Inline` and `NativeTerminal` modes.
- [ ] 9.2 Keep init, config, provider, model, and session picker applications in `FullScreen` mode.
- [ ] 9.3 Exit the session picker before a selected inline chat application starts.
- [ ] 9.4 Show a visible error when the selected chat application cannot start.
- [ ] 9.5 Restore cursor, input, mouse, paste, and terminal modes on normal, canceled, and failed exits.
- [ ] 9.6 Add command tests that prove each application selects its required presentation mode.

## 10. Package and Cross-Repository Integration

- [ ] 10.1 Run all Termina unit, compatibility, and native prototype gates.
- [ ] 10.2 Select a dotted SemVer prerelease that follows the Termina release process.
- [ ] 10.3 Publish the Termina prerelease package and record its package and commit links.
- [ ] 10.4 Update Netclaw to the prerelease with the repository package workflow.
- [ ] 10.5 Restore and build Netclaw against the published package, not a local binary.
- [ ] 10.6 Record explicit rollback steps for the package and the Netclaw presentation choice.

## 11. Verification and Completion

- [ ] 11.1 Add headless tests that inject every `SessionOutput` subtype.
- [ ] 11.2 Add typed-key tests for prompt, paste, history, Escape, approval, inspector, and copy flows.
- [ ] 11.3 Add a native chat smoke tape with typed input, paste, wheel input, resize, approval, and exit recovery.
- [ ] 11.4 Run `./scripts/smoke/run-smoke.sh light` and retain the result.
- [ ] 11.5 Run the focused Netclaw actor, protocol, CLI, and TUI test suites.
- [ ] 11.6 Run `dotnet slopwatch analyze` in each repository that contains code changes.
- [ ] 11.7 Run `./scripts/Add-FileHeaders.ps1 -Verify` for Netclaw C# changes.
- [ ] 11.8 Verify each issue acceptance criterion against tests or native evidence.
- [ ] 11.9 Run OpenSpec verification and resolve every mismatch.
- [ ] 11.10 Sync the approved delta specifications and archive the completed change.
