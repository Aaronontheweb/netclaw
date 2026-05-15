#!/usr/bin/env bash
# provider-rename.tape post-tape assertion.
#
# Validates that the TUI rename flow:
#   1) Removed 'seed-ollama' from the Providers map
#   2) Added 'renamed-ollama' with the same Type/Endpoint
#   3) `netclaw provider list --json` reflects the rename
#   4) `netclaw doctor` does not report new errors

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

echo "provider-rename: checking config file exists..."
if ! in_sandbox test -f "$config_path"; then
  echo "FAIL: ${config_path} does not exist." >&2
  exit 1
fi

fail=0

echo "provider-rename: checking key swap in netclaw.json..."
has_old="$(in_sandbox sh -lc "jq -r 'has(\"Providers\") and (.Providers | has(\"seed-ollama\"))' < '$config_path'" | tr -d '\r')"
has_new="$(in_sandbox sh -lc "jq -r 'has(\"Providers\") and (.Providers | has(\"renamed-ollama\"))' < '$config_path'" | tr -d '\r')"

if [[ "$has_old" == "true" ]]; then
  echo "FAIL: 'seed-ollama' still present in Providers." >&2
  fail=1
else
  echo "  ok  'seed-ollama' removed from Providers"
fi

if [[ "$has_new" != "true" ]]; then
  echo "FAIL: 'renamed-ollama' not present in Providers." >&2
  fail=1
else
  echo "  ok  'renamed-ollama' present in Providers"
fi

# Verify Type/Endpoint preserved across the rename.
renamed_type="$(in_sandbox sh -lc "jq -r '.Providers[\"renamed-ollama\"].Type // empty' < '$config_path'" | tr -d '\r')"
renamed_endpoint="$(in_sandbox sh -lc "jq -r '.Providers[\"renamed-ollama\"].Endpoint // empty' < '$config_path'" | tr -d '\r')"

if [[ "$renamed_type" != "ollama" ]]; then
  echo "FAIL: renamed-ollama.Type expected 'ollama', got '${renamed_type}'." >&2
  fail=1
else
  echo "  ok  renamed-ollama.Type preserved as 'ollama'"
fi

if [[ "$renamed_endpoint" != "http://ollama:11434" ]]; then
  echo "FAIL: renamed-ollama.Endpoint expected 'http://ollama:11434', got '${renamed_endpoint}'." >&2
  fail=1
else
  echo "  ok  renamed-ollama.Endpoint preserved"
fi

echo "provider-rename: cross-checking 'netclaw provider list'..."
list_output="$(in_sandbox netclaw provider list 2>/dev/null | tr -d '\r')"
if echo "$list_output" | grep -qE '^seed-ollama[[:space:]]'; then
  echo "FAIL: 'seed-ollama' still shown in provider list." >&2
  fail=1
else
  echo "  ok  'seed-ollama' absent from provider list"
fi
if ! echo "$list_output" | grep -qE '^renamed-ollama[[:space:]]+Ollama'; then
  echo "FAIL: 'renamed-ollama' missing from provider list." >&2
  echo "--- provider list ---" >&2
  echo "$list_output" >&2
  fail=1
else
  echo "  ok  'renamed-ollama' present in provider list"
fi

# Intentionally NOT running `netclaw doctor`. See provider-add.sh.

if (( fail )); then
  echo "--- netclaw.json contents ---" >&2
  in_sandbox cat "$config_path" >&2 || true
  exit 1
fi

echo "provider-rename: assertions passed."
