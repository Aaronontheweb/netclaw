#!/usr/bin/env bash
# Container daemon-lifecycle regression test for #1279.
#
# Verifies that the official image keeps a SINGLE supervised netclawd — that
# entrypoint.sh (PID 1) is the only thing that ever starts the daemon — and that
# config changes apply the way the rework intends:
#
#   Phase A — in-process config reload that actually takes effect:
#     Writing netclaw.json with a NEW bind port drives the daemon's
#     ConfigWatcherService to perform a coordinated in-process restart. The new
#     port must be serving and the old one gone (the Daemon-section change took
#     effect), while the process stays alive (SAME pid), keeps the lock, and
#     remains the entrypoint's child — no second daemon is spawned.
#
#   Phase B — `netclaw daemon start` under the supervisor:
#     The CLI must defer to the supervisor and refuse to spawn a detached
#     netclawd (the original #1279 bug), leaving exactly one daemon.
#
#   Phase C — a bad Daemon config fails loudly and recovers:
#     A semantically-invalid Daemon section (reverse-proxy bound to loopback) must
#     make the daemon abort startup (the supervisor observes the exit and
#     crash-loops) rather than silently keep serving stale config; fixing the
#     config on disk must let the supervisor's next restart recover.
#
# Usage:
#   scripts/docker/test-daemon-lifecycle.sh <image-ref>
#   scripts/docker/test-daemon-lifecycle.sh netclawd-pr:pr-1279
set -euo pipefail

IMAGE="${1:?usage: test-daemon-lifecycle.sh <image-ref>}"
CONTAINER="netclaw-lifecycle-1279"
DEFAULT_PORT=5199   # DaemonConfig default; the daemon binds this on first boot
NEW_PORT=5200       # Phase A re-binds here via a config-file write
PIDFILE=/home/netclaw/.netclaw/netclaw.pid
CONFIG=/home/netclaw/.netclaw/config/netclaw.json

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
# PID-file generation (line 2 = ISO start time); the daemon rewrites it on each restart.
daemon_generation() { docker exec "$CONTAINER" sh -c "sed -n 2p $PIDFILE 2>/dev/null" | tr -d '[:space:]'; }
# Number of times the supervisor has observed the daemon exit (proves a real exit
# vs an in-process restart, which keeps the process alive).
entrypoint_exit_count() { docker logs "$CONTAINER" 2>&1 | grep -c '\[entrypoint\] netclawd exited' || true; }

wait_healthy() {  # $1 = port, $2 = timeout-seconds
    for _ in $(seq 1 "$2"); do
        if docker exec "$CONTAINER" curl -fsS "http://127.0.0.1:$1/api/health/ready" >/dev/null 2>&1; then
            return 0
        fi
        [[ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null || echo false)" == "true" ]] \
            || fail "container exited while waiting for health on :$1"
        sleep 1
    done
    return 1
}
port_serving() { docker exec "$CONTAINER" curl -fsS "http://127.0.0.1:$1/api/health/ready" >/dev/null 2>&1; }

# Write netclaw.json into the container (stdin heredoc; -i keeps stdin open in CI).
write_config() { docker exec -i "$CONTAINER" sh -c "cat > $CONFIG"; }

echo "==> Starting supervised daemon from image: $IMAGE"
cleanup
# Ollama needs no API key, so this runs without secrets; the endpoint is never called
# during startup/health, so an unreachable one is fine. Local (loopback) mode is the
# default. NOTE: we deliberately do NOT set NETCLAW_Daemon__Port — env overrides the
# config file (Program.cs: env is highest priority), and Phase A needs the file's
# Daemon.Port to be authoritative. The default port is already 5199.
docker run -d --name "$CONTAINER" \
    -e NETCLAW_Providers__validate__Type=ollama \
    -e NETCLAW_Providers__validate__Endpoint=http://127.0.0.1:11434 \
    -e NETCLAW_Models__Main__Provider=validate \
    -e NETCLAW_Models__Main__ModelId=qwen2:0.5b \
    "$IMAGE" >/dev/null

wait_healthy "$DEFAULT_PORT" 60 || fail "supervised daemon never became healthy on :$DEFAULT_PORT"

count="$(daemon_count)"; pid="$(daemon_pid)"; ppid="$(daemon_ppid)"
echo "    initial: count=$count pid=$pid ppid=$ppid (port :$DEFAULT_PORT)"
[[ "$count" == "1" ]]  || fail "expected exactly 1 netclawd at startup, found $count"
[[ "$ppid" == "1" ]]   || fail "netclawd PPID is '$ppid', expected 1 (entrypoint supervisor)"

# ── Phase A: a config write reloads in-process AND the change takes effect ──
echo "==> Phase A: config write re-binds the daemon in-process (:$DEFAULT_PORT -> :$NEW_PORT)"
gen_before="$(daemon_generation)"
[[ -n "$gen_before" ]] || fail "daemon PID file has no start-time generation (line 2) at $PIDFILE"

# A Daemon-section change the watcher used to SKIP (#1279). Changing the bind port is
# the externally-observable proof that the reload actually re-read and re-bound config.
write_config <<JSON
{ "Daemon": { "Host": "127.0.0.1", "Port": $NEW_PORT, "ExposureMode": "local" } }
JSON

reloaded=false
for _ in $(seq 1 30); do
    gen_now="$(daemon_generation)"
    if [[ -n "$gen_now" && "$gen_now" != "$gen_before" ]]; then reloaded=true; break; fi
    [[ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null || echo false)" == "true" ]] \
        || fail "container exited during config-reload restart"
    sleep 1
done
[[ "$reloaded" == "true" ]] || fail "config write did not trigger an in-process restart (generation unchanged)"

# The change took effect: the new port serves and the old one is gone.
wait_healthy "$NEW_PORT" 60   || fail "daemon not healthy on the new port :$NEW_PORT after reload (re-bind did not take effect)"
! port_serving "$DEFAULT_PORT" || fail "old port :$DEFAULT_PORT still serving — the bind change did not apply"

# ...and it was an in-process restart, not a respawn / duplicate.
count_a="$(daemon_count)"; pid_a="$(daemon_pid)"; ppid_a="$(daemon_ppid)"
echo "    after reload: count=$count_a pid=$pid_a ppid=$ppid_a (port :$NEW_PORT)"
[[ "$count_a" == "1" ]]  || fail "config reload produced $count_a daemons (expected 1 — duplicate!)"
[[ "$pid_a" == "$pid" ]] || fail "PID changed ($pid -> $pid_a): the process exited instead of restarting in-process"
[[ "$ppid_a" == "1" ]]   || fail "netclawd PPID is '$ppid_a' after reload, expected 1"
[[ "$(entrypoint_exit_count)" == "0" ]] \
    || fail "entrypoint observed a daemon exit during an in-process reload (supervisor would respawn)"

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

# ── Phase C: a bad Daemon config fails loudly, then recovers when fixed ──────
echo "==> Phase C: bad Daemon config fails loudly (and recovers)"
exits_before="$(entrypoint_exit_count)"
# reverse-proxy bound to loopback is rejected by ExposureModeValidationService at
# startup — the rebuilt host aborts and exits rather than silently serving stale config.
write_config <<'JSON'
{ "Daemon": { "Host": "127.0.0.1", "ExposureMode": "reverse-proxy" } }
JSON

failed_loud=false
for _ in $(seq 1 45); do
    [[ "$(entrypoint_exit_count)" -gt "$exits_before" ]] && { failed_loud=true; break; }
    sleep 1
done
[[ "$failed_loud" == "true" ]] \
    || fail "bad Daemon config did not fail loudly — the supervisor never observed an exit (silently served stale config?)"
echo "    bad config -> daemon aborted startup (supervisor observed the exit)"

# Recover: write a good config; the supervisor's next restart reads it from disk.
write_config <<JSON
{ "Daemon": { "Host": "127.0.0.1", "Port": $NEW_PORT, "ExposureMode": "local" } }
JSON
wait_healthy "$NEW_PORT" 90 || fail "daemon did not recover on :$NEW_PORT after the bad config was fixed"
count_c="$(daemon_count)"; ppid_c="$(daemon_ppid)"
[[ "$count_c" == "1" ]] || fail "after recovery found $count_c daemons (expected 1)"
[[ "$ppid_c" == "1" ]]  || fail "after recovery netclawd PPID is '$ppid_c', expected 1"
echo "    recovered: count=$count_c ppid=$ppid_c (port :$NEW_PORT)"

echo "✓ #1279: single supervised daemon; reload re-binds in-process; daemon start defers; bad config fails loud + recovers"
