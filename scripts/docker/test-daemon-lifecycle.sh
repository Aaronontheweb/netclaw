#!/usr/bin/env bash
# Container daemon-lifecycle regression test for #1279.
#
# Verifies that the official image keeps a SINGLE supervised netclawd — that
# entrypoint.sh (PID 1) is the only thing that ever starts the daemon — across
# the two restart paths that used to (or could be feared to) split-brain:
#
#   Phase A — in-process config reload (the path a model/exposure change takes):
#     Writing netclaw.json drives the daemon's ConfigWatcherService to perform a
#     coordinated in-process restart (now incl. Daemon-section/exposure changes).
#     The process must stay alive (SAME pid), keep holding the lock, and remain the
#     entrypoint's child — the supervisor must NOT observe an exit and spawn a
#     second daemon.
#
#   Phase B — `netclaw daemon start` under the supervisor:
#     The CLI must defer to the supervisor and refuse to spawn a detached
#     netclawd (the original #1279 bug), leaving exactly one daemon.
#
# Usage:
#   scripts/docker/test-daemon-lifecycle.sh <image-ref>
#   scripts/docker/test-daemon-lifecycle.sh netclawd-pr:pr-1279
set -euo pipefail

IMAGE="${1:?usage: test-daemon-lifecycle.sh <image-ref>}"
CONTAINER="netclaw-lifecycle-1279"

cleanup() { docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

fail() {
    echo "ERROR: $*" >&2
    echo "---- container logs ----" >&2
    docker logs "$CONTAINER" >&2 2>&1 || true
    exit 1
}

# Count of supervised netclawd processes (0 on none, no stderr noise).
daemon_count() { docker exec "$CONTAINER" sh -c 'pgrep -x netclawd | wc -l' | tr -d '[:space:]'; }
# PID of the (first) netclawd, empty if none.
daemon_pid()   { docker exec "$CONTAINER" sh -c 'pgrep -x netclawd | head -n1' | tr -d '[:space:]'; }
# Parent PID of the (first) netclawd — must be 1 (entrypoint.sh), proving it is
# the supervisor's child and not an orphaned/exec-session process. Emits empty when
# no daemon is running (rather than letting `ps -p ""` error and trip `set -e` at the
# capture site, which would abort before the descriptive `fail` + log dump).
daemon_ppid()  { docker exec "$CONTAINER" sh -c 'pid=$(pgrep -x netclawd | head -n1); [ -n "$pid" ] && ps -o ppid= -p "$pid" || true' | tr -d '[:space:]'; }

wait_healthy() {
    for _ in $(seq 1 "$1"); do
        if docker exec "$CONTAINER" curl -fsS http://127.0.0.1:5199/api/health/ready >/dev/null 2>&1; then
            return 0
        fi
        [[ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null || echo false)" == "true" ]] \
            || fail "container exited while waiting for health"
        sleep 1
    done
    return 1
}

echo "==> Starting supervised daemon from image: $IMAGE"
cleanup
# Ollama needs no API key, so this runs without secrets. The endpoint is never
# called during startup/health, so an unreachable one is fine. Local (loopback)
# mode is the default — loopback auth lets the in-process restart POST through
# without a device token.
docker run -d --name "$CONTAINER" \
    -e NETCLAW_Daemon__Port=5199 \
    -e NETCLAW_Providers__validate__Type=ollama \
    -e NETCLAW_Providers__validate__Endpoint=http://127.0.0.1:11434 \
    -e NETCLAW_Models__Main__Provider=validate \
    -e NETCLAW_Models__Main__ModelId=qwen2:0.5b \
    "$IMAGE" >/dev/null

wait_healthy 60 || fail "supervised daemon never became healthy"

count="$(daemon_count)"; pid="$(daemon_pid)"; ppid="$(daemon_ppid)"
echo "    initial: count=$count pid=$pid ppid=$ppid"
[[ "$count" == "1" ]]  || fail "expected exactly 1 netclawd at startup, found $count"
[[ "$ppid" == "1" ]]   || fail "netclawd PPID is '$ppid', expected 1 (entrypoint supervisor)"

# ── Phase A: a config write must reload in-process, not respawn / duplicate ──
echo "==> Phase A: config-write reload (the path a model/exposure change takes)"
# The daemon rewrites its PID-file generation (line 2 = start time) on every restart,
# so a changed value proves the in-process reload actually happened.
pidfile=/home/netclaw/.netclaw/netclaw.pid
gen_before="$(docker exec "$CONTAINER" sh -c "sed -n 2p $pidfile 2>/dev/null" | tr -d '[:space:]')"
# Require a baseline generation so an empty value can't read as a spurious "changed".
[[ -n "$gen_before" ]] || fail "daemon PID file has no start-time generation (line 2) at $pidfile"

# Write a valid Local-mode Daemon section (a change the watcher used to SKIP — #1279).
# Port stays 5199 so the health probe keeps working after the restart.
docker exec -i "$CONTAINER" sh -c 'cat > /home/netclaw/.netclaw/config/netclaw.json' <<'JSON'
{ "Daemon": { "Host": "127.0.0.1", "Port": 5199, "ExposureMode": "local" } }
JSON

reloaded=false
for _ in $(seq 1 30); do
    gen_now="$(docker exec "$CONTAINER" sh -c "sed -n 2p $pidfile 2>/dev/null" | tr -d '[:space:]')"
    if [[ -n "$gen_now" && "$gen_now" != "$gen_before" ]]; then reloaded=true; break; fi
    [[ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null || echo false)" == "true" ]] \
        || fail "container exited during config-reload restart"
    sleep 1
done
[[ "$reloaded" == "true" ]] || fail "config write did not trigger an in-process restart (generation unchanged)"

wait_healthy 60 || fail "daemon did not become healthy again after config-reload restart"

count_a="$(daemon_count)"; pid_a="$(daemon_pid)"; ppid_a="$(daemon_ppid)"
echo "    after reload: count=$count_a pid=$pid_a ppid=$ppid_a"
[[ "$count_a" == "1" ]] || fail "config reload produced $count_a daemons (expected 1 — duplicate!)"
[[ "$pid_a" == "$pid" ]] || fail "PID changed ($pid -> $pid_a): the process exited instead of restarting in-process"
[[ "$ppid_a" == "1" ]]   || fail "netclawd PPID is '$ppid_a' after reload, expected 1"
if docker logs "$CONTAINER" 2>&1 | grep -q '\[entrypoint\] netclawd exited'; then
    fail "entrypoint observed a daemon exit during an in-process reload (supervisor would respawn)"
fi

# ── Phase B: `netclaw daemon start` must defer to the supervisor ────────────
echo "==> Phase B: 'netclaw daemon start' under supervisor"
# Capture output without letting a non-zero exit (e.g. a transient not-running blip,
# which returns exit 1) trip `set -e` before the assertion below runs.
out="$(docker exec "$CONTAINER" netclaw daemon start 2>&1)" || true
echo "    daemon start => $out"
echo "$out" | grep -qi "container supervisor" \
    || fail "'netclaw daemon start' did not defer to the supervisor: $out"

# Give any erroneously-spawned daemon time to race for the lock.
sleep 3

count_b="$(daemon_count)"; ppid_b="$(daemon_ppid)"
echo "    after daemon start: count=$count_b ppid=$ppid_b"
[[ "$count_b" == "1" ]] || fail "'netclaw daemon start' produced $count_b daemons (split-brain!)"
[[ "$ppid_b" == "1" ]]  || fail "netclawd PPID is '$ppid_b', expected 1 (daemon was orphaned)"
if docker logs "$CONTAINER" 2>&1 | grep -q "Another netclawd instance is already running (lock file held)"; then
    fail "lock-file contention detected in container logs (split-brain)"
fi

echo "✓ #1279: single supervised daemon across config reload and 'daemon start'; no lock contention"
