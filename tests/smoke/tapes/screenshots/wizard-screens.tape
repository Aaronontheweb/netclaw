# wizard-screens.tape (screenshot) — walk `netclaw init` and capture three
# settled wizard states as PNGs.
#
# Capture-only tape: NO post-tape assertion. The screenshots mode in
# run-smoke.sh compares each emitted PNG byte-for-byte against the committed
# baseline at tests/smoke/screenshots/<frame>.approved.png.
#
# Frames captured:
#   shot-wizard-provider-picker  — "Choose your LLM provider:" list
#   shot-wizard-security-posture — "Who will interact with this Netclaw
#                                   instance" posture list
#   shot-wizard-identity         — the "Agent name:" identity step
#
# Navigation + anchors mirror tapes/init-wizard.tape; see that file and
# src/Netclaw.Cli/Tui/Wizard/Steps/*StepView.cs for the synchronization
# rationale. Every Screenshot directive fires IMMEDIATELY after the
# Wait+Screen anchor that proves the final state rendered.
#
# Determinism note: these three screens carry no version string, timestamp,
# spinner, or token counter. The model-discovery screen IS skipped here —
# its "Connected! Found N models" line is transient and dynamic.
#
# Prepended preamble: tapes/screenshot-preamble.tape (determinism-pinned).

Output "/tmp/tape-shot-wizard-screens.gif"

# ─── Launch ──────────────────────────────────────────────────────────
Type "netclaw init"
Enter

# ─── Frame 1: Provider picker ────────────────────────────────────────
Wait+Screen@10s /Choose your LLM provider:/
Screenshot "/tmp/shot-wizard-provider-picker.png"

# Provider list ordering is alphabetical by TypeKey:
#   anthropic, ollama, openai, openai-compatible, openrouter
# Anthropic is the default; one Down lands on Ollama.
Down
Enter

# Endpoint input — native Ollama is at http://localhost:11434, which is
# also the prompt's default value.
Wait+Screen@10s /endpoint:/
# VHS has no End/Home — push the cursor right past the existing text
# (Right N is a no-op at end), then Backspace to clear all 22 chars.
Right 32
Backspace 32
Type "http://localhost:11434"
Enter

# Connection probe + model discovery. The "Connected! Found N models"
# success frame auto-advances; wait directly on the model list header.
Wait+Screen@45s /Select a model/
# Models are alphabetical: all-minilm first, qwen2 second.
Down
Enter

# ─── Frame 2: Security posture ───────────────────────────────────────
Wait+Screen@10s /Who will interact with this Netclaw instance/
Screenshot "/tmp/shot-wizard-security-posture.png"
# Personal is the first / default-highlighted option.
Enter

# ─── Channel picker ──────────────────────────────────────────────────
Wait+Screen@10s /Which channels would you like to connect/
# 'd' = done without selecting any channels.
Type "d"

# ─── Web search ──────────────────────────────────────────────────────
Wait+Screen@10s /Choose your web search provider/
# DuckDuckGo is the first / default option.
Enter

# ─── Browser automation ──────────────────────────────────────────────
Wait+Screen@10s /Enable browser automation/
# "No" is the first / default option.
Enter

# ─── Frame 3: Identity (Agent name) ──────────────────────────────────
Wait+Screen@10s /Agent name:/
Screenshot "/tmp/shot-wizard-identity.png"

# We have the three frames we came for; abandon the wizard. Ctrl+C drops
# out of the interactive prompt back to the shell — there is no config to
# validate, this is a capture-only tape.
Ctrl+C
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
