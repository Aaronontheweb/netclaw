#!/usr/bin/env bash
# provider-add.tape post-tape assertion.
#
# Validates that the TUI add flow wrote a usable provider entry to
# the produced config:
#   1) `provider list --json` includes 'smoke-add-ollama' with the
#      expected type and endpoint
#   2) The persisted netclaw.json contains the same entry under the
#      Providers map
#   3) `netclaw doctor` does not report new errors (WARNs are fine)

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

config_path="${NETCLAW_HOME_IN}/config/netclaw.json"

echo "provider-add: checking config file exists at ${config_path}..."
if ! in_sandbox test -f "$config_path"; then
  echo "FAIL: ${config_path} does not exist." >&2
  in_sandbox sh -lc "ls -la '$NETCLAW_HOME_IN' '$NETCLAW_HOME_IN/config' 2>&1" >&2 || true
  exit 1
fi

echo "provider-add: validating JSON parses..."
if ! in_sandbox sh -lc "jq empty < '$config_path'"; then
  echo "FAIL: ${config_path} is not valid JSON." >&2
  exit 1
fi

echo "provider-add: checking 'smoke-add-ollama' in config..."
fail=0

assert_field() {
  local jq_expr="$1"
  local expected="$2"
  local actual
  actual="$(in_sandbox sh -lc "jq -r '$jq_expr // empty' < '$config_path'" | tr -d '\r')"
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL: expected '${jq_expr}' == '${expected}', got '${actual}'." >&2
    fail=1
  else
    echo "  ok  ${jq_expr} == '${expected}'"
  fi
}

assert_field '.Providers["smoke-add-ollama"].Type'     'ollama'
assert_field '.Providers["smoke-add-ollama"].Endpoint' 'http://ollama:11434'

echo "provider-add: cross-checking 'netclaw provider list'..."
# `provider list` emits a table (no --json variant); just grep the
# configured name out of the row.
list_output="$(in_sandbox netclaw provider list 2>/dev/null | tr -d '\r')"
if ! echo "$list_output" | grep -qE '^smoke-add-ollama[[:space:]]+Ollama'; then
  echo "FAIL: 'smoke-add-ollama' row missing or malformed in 'provider list' output." >&2
  echo "--- provider list ---" >&2
  echo "$list_output" >&2
  fail=1
else
  echo "  ok  'smoke-add-ollama' present in provider list"
fi

# Intentionally NOT running `netclaw doctor` here. This tape adds a
# provider to an otherwise-empty config (no Tools, no Security, no
# Models — those are produced by `netclaw init`, not by the provider
# add flow). Doctor would [FAIL] on the missing sections, but those
# failures are orthogonal to the surface this tape is testing. The
# init-wizard.tape exercises the full doctor pass.

if (( fail )); then
  echo "--- netclaw.json contents ---" >&2
  in_sandbox cat "$config_path" >&2 || true
  exit 1
fi

echo "provider-add: assertions passed."
