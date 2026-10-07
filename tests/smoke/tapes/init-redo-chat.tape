# init-redo-chat.tape — existing-install "Redo identity setup" hands off to the
# guided identity chat.
#
# Runs the bootstrap wizard once against the smoke LLM to create a real install
# (provider, daemon, identity), leaves the first chat, then re-enters
# `netclaw init`, picks "Redo identity setup", saves a new identity and takes the
# default completion action (Enter = "Start guided identity chat").
#
# The onboarding trigger is hidden in the chat UI, and the first chat in this tape
# already printed the same smoke reply, so the screen alone proves little. The tape
# counts the daemon's completed LLM calls (per-tape NETCLAW_HOME log) before the redo
# and after the redo chat; the assertion script requires the count to grow. The tape also snapshots netclaw.json
# before the redo and compares it after, so a redo that rewrites config fails here.
#
# Pairs with tests/smoke/assertions/init-redo-chat.sh (updated identity on disk).
# The skip path (Esc, no chat) is covered by init-redo-identity.tape.
#
# No escaped double-quotes (VHS v0.11 limitation).

Output "/tmp/tape-init-redo-chat.gif"

# ─── First run: bootstrap wizard creates the install ────────────────
Type "netclaw init"
Enter

Wait+Screen@10s /Choose your LLM provider:/
Down 5
Enter

Wait+Screen@10s /endpoint:/
Right 32
Backspace 32
Type "__SMOKE_LLM_ENDPOINT__"
Enter

Wait+Screen@10s /API key \(optional\)/
Enter

Wait+Screen@45s /Select a model/
Enter

Wait+Screen@10s /Agent name:/
Enter
Wait+Screen@10s /Communication style:/
Enter
Wait+Screen@10s /Your name:/
Type "SmokeTester"
Enter
Wait+Screen@10s /Your timezone:/
Enter

Wait+Screen@10s /Who will interact with this Netclaw instance/
Enter

Wait+Screen@60s /System: Session started\./
Ctrl+Q
Wait+Screen@10s /TAPE\$/

# ─── Snapshot config, then clear the screen so no earlier chat text matches ──
Type "cp $NETCLAW_HOME/config/netclaw.json $NETCLAW_HOME/netclaw.before.json && grep -c LLM.streaming.call.completed $NETCLAW_HOME/logs/daemon-*.log > $NETCLAW_HOME/llm.before && clear"
Enter
Wait+Screen@5s /TAPE\$/

# ─── Redo identity setup ────────────────────────────────────────────
Type "netclaw init"
Enter
Wait+Screen@15s /Existing Netclaw install detected/

# "Redo identity setup" is the first menu item.
Enter
Wait+Screen@10s /Agent name:/

Type "Sentinel"
Enter
Wait+Screen@10s /Communication style:/
Enter
Wait+Screen@10s /Your name:/
Type "Pat"
Enter
Wait+Screen@10s /Your timezone:/
Enter

# Saved screen offers the guided chat as the default action.
Wait+Screen@10s /Start guided identity chat/
Enter

# The chat opens and answers the hidden onboarding turn.
Wait+Screen@60s /System: Session started\./
Wait+Screen@60s /Netclaw: Netclaw smoke response\./
Ctrl+Q
Wait+Screen@10s /TAPE\$/

# ─── Count requests after the redo chat ─────────────────────────────
Type "grep -c LLM.streaming.call.completed $NETCLAW_HOME/logs/daemon-*.log > $NETCLAW_HOME/llm.after"
Enter
Wait+Screen@5s /TAPE\$/

# ─── Redo must not touch config ─────────────────────────────────────
Type "cmp -s $NETCLAW_HOME/config/netclaw.json $NETCLAW_HOME/netclaw.before.json && echo CONFIG_UNCHANGED"
Enter
Wait+Screen@5s /CONFIG_UNCHANGED/

Type "exit"
Enter
