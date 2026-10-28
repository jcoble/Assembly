#!/usr/bin/env bash
# Tests that dotnet/scripts/desktop.sh uses the lane's own folders (scripts/lane-env.sh): in a checkout that has no
# .lane and isn't on main, it stops before it writes anything; in a lane's checkout, serve writes only under that
# lane's data folder and starts the backend on the lane's port, home and token file. Each case runs this checkout's
# desktop.sh and lane-env.sh in a throwaway worktree with HOME set to a temp folder, so it can never reach the real
# ~/assembly-dotnet, and with pnpm, node, dotnet and nc replaced by stubs, so it never downloads adapters or starts a
# real backend.
# Run: bash scripts/desktop-lane.test.sh
set -uo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
branches=()
cleanup() {
  local b
  for tree in "$tmp"/tree-*; do [ -d "$tree" ] && git -C "$repo" worktree remove --force "$tree" >/dev/null 2>&1; done
  git -C "$repo" worktree prune
  for b in ${branches[@]+"${branches[@]}"}; do git -C "$repo" branch -D "$b" >/dev/null 2>&1; done
  rm -rf "$tmp"
}
trap cleanup EXIT
failed=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; failed=1; fi; }
# worktree <name>: a throwaway worktree with this checkout's desktop.sh and lane-env.sh
worktree() {
  local branch=desktop-lane-test-$1-$$
  branches+=("$branch")
  git -C "$repo" worktree add -q -b "$branch" "$tmp/tree-$1" HEAD || { echo "FAIL couldn't make the worktree"; exit 1; }
  cp "$repo/dotnet/scripts/desktop.sh" "$tmp/tree-$1/dotnet/scripts/desktop.sh"
  cp "$repo/scripts/lane-env.sh" "$tmp/tree-$1/scripts/lane-env.sh"
}

# A checkout with no .lane, off main: lane-env's stop, and nothing written. The stubs all fail.
home=$tmp/home-none stubs=$tmp/bin-none
mkdir -p "$home" "$stubs"
for tool in pnpm node dotnet nc; do printf '#!/bin/sh\nexit 97\n' >"$stubs/$tool"; chmod +x "$stubs/$tool"; done
worktree none
out=$(cd "$tmp/tree-none" && HOME=$home PATH=$stubs:$PATH dotnet/scripts/desktop.sh serve 2>&1)
code=$?
check "no .lane: it exits non-zero (exit $code)" '[ "$code" != 0 ]'
check "no .lane: it prints lane-env's stop line" 'grep -q "^stop: " <<<"$out"'
check "no .lane: it writes nothing under HOME" '[ -z "$(ls -A "$home")" ]'
[ "$failed" = 0 ] || printf 'its output:\n%s\nunder HOME:\n%s\n' "$out" "$(find "$home" | head -20)"

# A lane's checkout (probe, 9): serve runs to the end on stubs. dotnet publish makes a backend that records its
# arguments and exits; nc answers at once.
home=$tmp/home-probe stubs=$tmp/bin-probe args=$tmp/probe-args
mkdir -p "$home" "$stubs"
for tool in pnpm node nc; do printf '#!/bin/sh\nexit 0\n' >"$stubs/$tool"; chmod +x "$stubs/$tool"; done
cat >"$stubs/dotnet" <<EOF
#!/bin/sh
while [ \$# -gt 0 ]; do [ "\$1" = -o ] && bin=\$2; shift; done
mkdir -p "\$bin"
printf '#!/bin/sh\necho "\$*" > $args\n' > "\$bin/Assembly.Desktop"
chmod +x "\$bin/Assembly.Desktop"
EOF
chmod +x "$stubs/dotnet"
worktree probe
printf 'LANE_NAME=probe\nLANE_NUMBER=9\n' >"$tmp/tree-probe/.lane"
out=$(cd "$tmp/tree-probe" && HOME=$home PATH=$stubs:$PATH dotnet/scripts/desktop.sh serve 2>&1)
code=$?
for _ in $(seq 50); do [ -s "$args" ] && break; sleep 0.1; done
data=$home/assembly-dotnet-probe
before=$failed
check "a lane: it exits 0 (exit $code)" '[ "$code" = 0 ]'
check "a lane: it writes mac/agents.json under the lane's data" '[ -f "$data/mac/agents.json" ]'
check "a lane: it writes the pid file under the lane's data" '[ -f "$data/mac/backend.pid" ]'
check "a lane: it makes the test repo under the lane's data" '[ -d "$data/fixtures/notes-repo/.git" ]'
check "a lane: nothing is under main's ~/assembly-dotnet/mac" '[ ! -e "$home/assembly-dotnet/mac" ]'
want="serve --port $((18797 + 90)) --home $data/mac --token-file $home/.config/assembly-dotnet-probe/env"
check "a lane: the backend starts on the lane's port, home and token file" '[ "$(cat "$args" 2>/dev/null)" = "$want" ]'
[ "$failed" = "$before" ] || printf 'its output:\n%s\nthe backend got: %s\nwanted:          %s\nunder HOME:\n%s\n' \
  "$out" "$(cat "$args" 2>/dev/null)" "$want" "$(find "$home" -maxdepth 3 | head -30)"

# The remote: a lane whose .lane has REMOTE starts the backend with --remote, its box port and its box token file; a
# --remote on the command line replaces it; with neither there is no --remote.
# serve_args <name> <.lane text> [serve arguments]: runs serve in a new lane worktree and prints the backend's arguments.
serve_args() {
  local name=$1 lane=$2 h=$tmp/home-$1 a=$tmp/args-$1
  shift 2
  mkdir -p "$h"
  worktree "$name"
  printf "$lane" >"$tmp/tree-$name/.lane"
  mkdir -p "$stubs-$name" && cp "$stubs"/* "$stubs-$name"/ && sed "s|$args|$a|" "$stubs/dotnet" >"$stubs-$name/dotnet"
  (cd "$tmp/tree-$name" && HOME=$h PATH=$stubs-$name:$PATH dotnet/scripts/desktop.sh serve "$@" >/dev/null 2>&1)
  for _ in $(seq 50); do [ -s "$a" ] && break; sleep 0.1; done
  cat "$a" 2>/dev/null
}
rtail() { echo "--remote-port $((9797 + 90)) --remote-token-file .config/assembly-dotnet-$1/env"; }
got=$(serve_args rem 'LANE_NAME=rem\nLANE_NUMBER=9\nREMOTE=workbox-test\n')
check "a REMOTE in .lane reaches the backend's command line" '[[ $got == *" --remote workbox-test $(rtail rem)" ]]'
got=$(serve_args over 'LANE_NAME=over\nLANE_NUMBER=9\nREMOTE=workbox-test\n' --remote other-box)
check "--remote overrides the .lane's REMOTE" '[[ $got == *" --remote other-box $(rtail over)" && $got != *workbox-test* ]]'
got=$(serve_args bad 'LANE_NAME=bad\nLANE_NUMBER=9\n' --remote -oProxyCommand=x)
check "a --remote that isn't an ssh alias of that kind stops before the backend starts" '[ -z "$got" ]'
got=$(serve_args none2 'LANE_NAME=none2\nLANE_NUMBER=9\n')
check "with no remote set the backend gets no --remote" '[[ -n $got && $got != *--remote* ]]'
[ "$failed" = 0 ] || printf 'the backend got: %s\n' "$got"
exit "$failed"
