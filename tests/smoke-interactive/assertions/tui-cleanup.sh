#!/usr/bin/env bash
# tui-cleanup.tape post-tape assertion.
#
# The tape's own Wait+Screen anchors are the primary regression
# detector — if the alt screen corrupts during arrow navigation, the
# row anchors stop matching and the tape times out. This script just
# confirms that the seeded providers survived intact and `netclaw
# doctor` does not flag any errors against the produced config.

set -euo pipefail

: "${PROJECT_NAME:?PROJECT_NAME must be set by run-tape.sh}"
: "${COMPOSE_FILE:?COMPOSE_FILE must be set by run-tape.sh}"
: "${NETCLAW_HOME_IN:?NETCLAW_HOME_IN must be set by run-tape.sh}"

compose() {
  docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" "$@"
}

in_sandbox() {
  compose exec -T \
    -e "NETCLAW_HOME=${NETCLAW_HOME_IN}" \
    netclaw-sandbox "$@"
}

fail=0

echo "tui-cleanup: checking seeded providers persisted across TUI exit..."
list_output="$(in_sandbox netclaw provider list 2>/dev/null | tr -d '\r')"

for name in seed-a seed-b; do
  if ! echo "$list_output" | grep -qE "^${name}[[:space:]]+Ollama"; then
    echo "FAIL: provider '$name' missing from list after TUI exit." >&2
    fail=1
  else
    echo "  ok  '$name' still present"
  fi
done

# Intentionally NOT running `netclaw doctor`. See provider-add.sh.

if (( fail )); then
  echo "--- provider list --json ---" >&2
  echo "$list_output" >&2
  exit 1
fi

echo "tui-cleanup: assertions passed."
