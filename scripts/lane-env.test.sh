#!/usr/bin/env bash
# Tests for scripts/lane-env.sh: a checkout with no .lane gets main's ports, folders and box unit, so only main may
# be one. Sources it in a throwaway repo on main, on another branch and detached, with and without a .lane, and
# checks it passes (exit 0) or stops (exit 1). Run: bash scripts/lane-env.test.sh
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(mktemp -d)
trap 'rm -rf "$repo"' EXIT
git -C "$repo" init -q -b main
mkdir -p "$repo/scripts"
cp "$here/lane-env.sh" "$repo/scripts/"
git -C "$repo" add -A
git -C "$repo" -c user.name=t -c user.email=t@t commit -q -m base
failed=0
# expect <exit code> <name>: sources lane-env.sh in the repo as the scripts do, under set -e.
expect() {
  (cd "$repo" && bash -ec '. scripts/lane-env.sh; echo "$BOX_UNIT"') >/dev/null 2>&1
  local got=$?
  if [ "$got" = "$1" ]; then echo "ok   $2"; else echo "FAIL $2: exit $got, wanted $1"; failed=1; fi
}
expect 0 "main with no .lane gets main's values"
git -C "$repo" checkout -q -b lane/x
expect 1 "another branch with no .lane stops"
printf 'LANE_NAME=x\nLANE_NUMBER=2\n' >"$repo/.lane"
expect 0 "another branch with a .lane gets the lane's values"
rm "$repo/.lane"
git -C "$repo" checkout -q --detach
expect 1 "a detached checkout with no .lane stops"
exit $failed
