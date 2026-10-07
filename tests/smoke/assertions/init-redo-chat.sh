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
if ! grep -Fq "Sentinel" "$SOUL_PATH" 2>/dev/null; then
  echo "FAIL: ${SOUL_PATH} does not contain the redone agent name 'Sentinel'." >&2
  assert_fail=1
else
  echo "  ok  SOUL.md has the updated agent name"
fi
if ! grep -Fq "Pat" "$SOUL_PATH" 2>/dev/null; then
  echo "FAIL: ${SOUL_PATH} does not contain the redone user name 'Pat'." >&2
  assert_fail=1
else
  echo "  ok  SOUL.md has the updated user name"
fi

echo "init-redo-chat: checking the provider survived the redo..."
config_json="$(read_config_json)"
assert_field '.Providers["openai-compatible"].Type' 'openai-compatible' "$config_json" || :

if (( assert_fail )); then
  exit 1
fi

echo "init-redo-chat: assertions passed (identity updated, provider preserved)."
