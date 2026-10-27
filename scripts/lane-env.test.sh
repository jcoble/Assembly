#!/usr/bin/env bash
# Tests for scripts/lane-env.sh: a checkout with no .lane gets main's ports, folders and box unit, so only main may
# be one, and a .lane needs a LANE_NAME and a LANE_NUMBER from 1 to 9. Sources it in a throwaway repo on main, on another branch and detached, with and without a .lane, and
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
# expect <exit code> <name> [values]: sources lane-env.sh in the repo as the scripts do, under set -e, and checks the
# values it resolved when given.
expect() {
  local out got
  out=$(cd "$repo" && bash -ec '. scripts/lane-env.sh; echo "$DESKTOP_PORT $BOX_PORT $BOX_UNIT $DATA $BOX_DATA"' 2>/dev/null)
  got=$?
  if [ "$got" = "$1" ] && { [ -z "${3:-}" ] || [ "$out" = "$3" ]; }; then echo "ok   $2"
  else echo "FAIL $2: exit $got, wanted $1; values '$out'"; failed=1; fi
}
expect 0 "main with no .lane gets main's values" "18797 9797 assembly-dotnet $HOME/assembly-dotnet assembly-dotnet"
git -C "$repo" checkout -q -b lane/x
expect 1 "another branch with no .lane stops"
printf 'LANE_NAME=x\nLANE_NUMBER=2\n' >"$repo/.lane"
expect 0 "another branch with a .lane gets the lane's values" "18817 9817 assembly-dotnet-x $HOME/assembly-dotnet-x assembly-dotnet-x"
for lane in 'LANE_NAME=\nLANE_NUMBER=2' 'LANE_NAME=x' 'LANE_NAME=x\nLANE_NUMBER=0' 'LANE_NAME=x\nLANE_NUMBER=10' 'LANE_NAME=x\nLANE_NUMBER=a' \
  'LANE_NAME=x/../assembly-dotnet\nLANE_NUMBER=2' 'LANE_NAME=-x\nLANE_NUMBER=2'; do
  printf "$lane\n" >"$repo/.lane"
  expect 1 "a .lane with $(printf "$lane" | tr '\n' ' ') stops"
done
rm "$repo/.lane"
git -C "$repo" checkout -q --detach
expect 1 "a detached checkout with no .lane stops"
exit $failed
