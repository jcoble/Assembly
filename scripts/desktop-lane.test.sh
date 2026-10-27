#!/usr/bin/env bash
# Tests that dotnet/scripts/desktop.sh uses the lane's own folders (scripts/lane-env.sh): in a checkout that has no
# .lane and isn't on main, it stops before it writes anything. It runs in a throwaway worktree with HOME set to a temp
# folder, so it can never reach the real ~/assembly-dotnet, and with pnpm, node, dotnet and nc replaced by stubs that
# fail, so it can never download adapters or start a backend.
# Run: bash scripts/desktop-lane.test.sh
set -uo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
tree=$tmp/tree home=$tmp/home stubs=$tmp/bin branch=desktop-lane-test-$$
cleanup() {
  git -C "$repo" worktree remove --force "$tree" >/dev/null 2>&1
  git -C "$repo" worktree prune
  git -C "$repo" branch -D "$branch" >/dev/null 2>&1
  rm -rf "$tmp"
}
trap cleanup EXIT
mkdir -p "$home" "$stubs"
for tool in pnpm node dotnet nc; do printf '#!/bin/sh\nexit 97\n' >"$stubs/$tool"; chmod +x "$stubs/$tool"; done
git -C "$repo" worktree add -q -b "$branch" "$tree" HEAD || { echo "FAIL couldn't make the worktree"; exit 1; }

out=$(cd "$tree" && HOME=$home PATH=$stubs:$PATH dotnet/scripts/desktop.sh serve 2>&1)
code=$?
failed=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; failed=1; fi; }
check "it exits non-zero (exit $code)" '[ "$code" != 0 ]'
check "it prints lane-env's stop line" 'grep -q "^stop: " <<<"$out"'
check "it writes nothing under HOME" '[ -z "$(ls -A "$home")" ]'
[ "$failed" = 0 ] || printf 'its output:\n%s\nunder HOME:\n%s\n' "$out" "$(find "$home" | head -20)"
exit "$failed"
