#!/usr/bin/env bash
# Runs a journey in the bench's window (scripts/bench.sh up first), once per case: Claude, then Codex, on each machine.
#   scripts/journey.sh <name> [local|workbox-test|all]     (default all)
# Each case writes report.md, result.json, video.mp4 and app-log.txt (and on a failure fail.png) under
# $DATA/journeys/<utc>-<name>/<agent>-<machine>/; summary.md has one line per case.
# A case whose login the bench found failing is BLOCKED: provider without running.
# Exit codes: 0 every case passed, 1 a case failed, 3 nothing failed but a case was blocked.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lane-env.sh
export PATH=$HOME/.local/bin:$PATH
name=${1:?usage: scripts/journey.sh <name> [local|workbox-test|all]}
where=${2:-all}
[ -f "scripts/journeys/$name.ts" ] || { echo "no journey scripts/journeys/$name.ts" >&2; exit 2; }
case "$where" in local|workbox-test) machines=("$where") ;; all) machines=(local workbox-test) ;; *) echo "unknown machine $where" >&2; exit 2 ;; esac
[ -f "$DATA/bench/env" ] && [ -f "$DATA/bench/state.json" ] || { echo 'the bench is not up; run scripts/bench.sh up' >&2; exit 1; }
. "$DATA/bench/env"
export BENCH_STATE DISPLAY DBUS_SESSION_BUS_ADDRESS
out=$DATA/journeys/$(date -u +%Y%m%dT%H%M%SZ)-$name
mkdir -p "$out"
summary=$out/summary.md
printf '# %s\n\nsource %s, served %s\n\n' "$name" "$(node -p "require('$BENCH_STATE').sourceCommit")" "$(node -p "require('$BENCH_STATE').servedCommit")" > "$summary"
failed=0 blocked=0
for machine in "${machines[@]}"; do
  for agent in claude codex; do
    case_out=$out/$agent-$machine
    reason=$(node -p "require('$BENCH_STATE').blocked['$agent@$machine'] ?? ''")
    started=$SECONDS
    if [ -n "$reason" ]; then
      line="BLOCKED $agent on $machine: provider: $reason"
    else
      code=0
      line=$(node "scripts/journeys/$name.ts" "$agent" "$machine" "$case_out" | tail -1) || code=$?
      [ -n "$line" ] || line="FAIL $agent on $machine: the journey ended without a result (exit $code)"
    fi
    case "$line" in PASS*) ;; BLOCKED*) blocked=1 ;; *) failed=1 ;; esac
    echo "$line ($((SECONDS - started)) s)"
    printf -- '- %s (%s s)\n' "$line" "$((SECONDS - started))" >> "$summary"
  done
done
echo "summary: $summary"
[ "$failed" = 0 ] || exit 1
[ "$blocked" = 0 ] || exit 3
