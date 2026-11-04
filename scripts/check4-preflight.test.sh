#!/usr/bin/env bash
# A mismatch must stop before profile setup, evidence directories, fixtures or provider sessions.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/scripts" "$fixture/bin"
cp "${CHECK4_SCRIPT:-$here/scripts/check4.sh}" "$fixture/scripts/check4.sh"
cp "$here/scripts/lane-env.sh" "$fixture/scripts/"
git -C "$fixture" init -q -b main
git -C "$fixture" -c user.name=Test -c user.email=test@example.invalid commit --allow-empty -qm fixture
cat > "$fixture/.lane" <<'LANE'
LANE_NAME=throwaway
LANE_NUMBER=8
LANE
cat > "$fixture/bin/node" <<'NODE'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CHECK4_CALLS"
echo 'backend mismatch: remote aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; desktop bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' >&2
exit 1
NODE
chmod +x "$fixture/bin/node"
printf '#!/bin/sh\nexit 1\n' > "$fixture/bin/ssh"
chmod +x "$fixture/bin/ssh"
printf '/out\n/calls\n' > "$fixture/.gitignore"
git -C "$fixture" add .
git -C "$fixture" -c user.name=Test -c user.email=test@example.invalid commit -qm scripts
if (cd "$fixture" && HOME="$fixture" CHECK4_CALLS="$fixture/calls" PATH="$fixture/bin:$PATH" bash scripts/check4.sh > "$fixture/out" 2>&1); then
  echo 'FAIL: check4 accepted mismatched backend'; exit 1
fi
[ "$(wc -l < "$fixture/calls")" -eq 1 ]
[ "$(cat "$fixture/calls")" = 'scripts/check-remote.ts throwaway 8 HEAD' ] || { echo "FAIL: first command was $(cat "$fixture/calls"), expected the identity preflight"; exit 1; }
[ ! -e "$fixture/evidence" ]
rg -q 'remote a+; desktop b+' "$fixture/out"
echo 'ok check4 refuses mismatched backend before setup or tests'
