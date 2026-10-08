#!/usr/bin/env bash
# init-redo-chat.tape post-tape assertion.
#
# The tape proves the chat opened and replied after the redo. This script proves
# the redo saved the UPDATED identity that the guided interview is built from,
# and that the provider the wizard configured is still in place.

set -euo pipefail

. "$(dirname "$0")/_lib.sh"

assert_fail=0

echo "init-redo-chat: checking the redo wrote the updated identity..."
if ! grep -Fxq "# You are Sentinel" "$SOUL_PATH" 2>/dev/null; then
  echo "FAIL: ${SOUL_PATH} does not have the redone agent name exactly 'Sentinel'." >&2
  assert_fail=1
else
  echo "  ok  SOUL.md has the updated agent name"
fi
if ! grep -Fxq -- "- Name: Pat" "$SOUL_PATH" 2>/dev/null; then
  echo "FAIL: ${SOUL_PATH} does not have the redone user name exactly 'Pat'." >&2
  assert_fail=1
else
  echo "  ok  SOUL.md has the updated user name"
fi

echo "init-redo-chat: checking the redo left netclaw.json untouched..."
if [[ ! -s "${NETCLAW_HOME}/netclaw.before.json" ]]; then
  echo "FAIL: ${NETCLAW_HOME}/netclaw.before.json missing — the tape never snapshotted config." >&2
  assert_fail=1
elif cmp -s "$CONFIG_PATH" "${NETCLAW_HOME}/netclaw.before.json"; then
  echo "  ok  netclaw.json unchanged by the redo"
else
  echo "FAIL: netclaw.json changed during the redo — redo must not call WriteConfig." >&2
  assert_fail=1
fi

echo "init-redo-chat: checking the redo chat sent a turn to the model (daemon log count)..."
before="$(tr -d '[:space:]' < "${NETCLAW_HOME}/llm.before" 2>/dev/null || true)"
after="$(tr -d '[:space:]' < "${NETCLAW_HOME}/llm.after" 2>/dev/null || true)"
if [[ ! "$before" =~ ^[0-9]+$ || ! "$after" =~ ^[0-9]+$ ]]; then
  echo "FAIL: LLM call counts unreadable (before='${before}' after='${after}')." >&2
  assert_fail=1
elif (( after > before )); then
  echo "  ok  daemon LLM calls grew (${before} -> ${after})"
else
  echo "FAIL: no new daemon LLM call after the redo chat started (${before} -> ${after})." >&2
  assert_fail=1
fi

echo "init-redo-chat: checking the provider survived the redo..."
config_json="$(read_config_json)"
assert_field '.Providers["openai-compatible"].Type' 'openai-compatible' "$config_json" || :

if (( assert_fail )); then
  exit 1
fi

echo "init-redo-chat: assertions passed (identity updated, provider preserved)."
