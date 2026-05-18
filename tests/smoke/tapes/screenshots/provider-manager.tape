# provider-manager.tape (screenshot) — drive the `netclaw provider` TUI and
# capture two settled states as PNGs.
#
# Capture-only tape: NO post-tape assertion. The screenshots mode in
# run-smoke.sh compares each emitted PNG byte-for-byte against the committed
# baseline at tests/smoke/screenshots/<frame>.approved.png.
#
# Frames captured:
#   shot-provider-manager-empty     — the provider list with no providers
#                                     configured (every type as "(not
#                                     configured)" + "+ Add new provider...")
#   shot-provider-manager-add-name  — the "Name your provider" add step
#
# Navigation + anchors mirror tapes/provider-add.tape; see that file and
# src/Netclaw.Cli/Tui/ProviderManagerPage.cs for the synchronization
# rationale (including the short Termina-relayout `Sleep 300ms` guards).
# Every Screenshot directive fires IMMEDIATELY after the Wait+Screen anchor.
#
# Determinism note: the empty provider list and the AddName step are static
# (no version string, timestamp, spinner, or token counter). The async
# probe / "ready to save" frame is NOT captured here.
#
# Prepended preamble: tapes/screenshot-preamble.tape (determinism-pinned).

Output "/tmp/tape-shot-provider-manager.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw provider"
Enter

# ─── Frame 1: Empty provider list ────────────────────────────────────
# With no providers configured the list shows every known type as a
# "(not configured)" row plus the "+ Add new provider..." sentinel.
# Anchor on "Ollama" (a guaranteed type row) so we know the list rendered.
Wait+Screen@10s /Ollama/
Sleep 300ms
Screenshot "/tmp/shot-provider-manager-empty.png"

# Provider type list (alphabetical by TypeKey): anthropic, ollama, openai,
# openai-compatible, openrouter. Anthropic is highlighted by default; one
# Down lands on Ollama. Activating a type row goes straight to AddName.
Down
Enter

# ─── Frame 2: Name your provider ─────────────────────────────────────
Wait+Screen@10s /Name your provider/
Sleep 300ms
Screenshot "/tmp/shot-provider-manager-add-name.png"

# We have the two frames we came for; quit the TUI back to the shell.
Ctrl+Q
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
