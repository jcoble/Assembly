#!/usr/bin/env bash
# Behavioral checks for scripts/lane-reap.sh: reaps clean merged lanes (including merged only into origin/main)
# and leaves dirty, unmerged, and busy lanes intact.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
fixture=$(mktemp -d)
fixture=$(cd "$fixture" && pwd -P)
children=()
cleanup() {
  for pid in "${children[@]}"; do
    if kill -0 "$pid" 2>/dev/null; then kill "$pid"; fi
    wait "$pid" 2>/dev/null || true
  done
  rm -rf "$fixture"
}
trap cleanup EXIT

fixture_home=$fixture/home
export GIT_CONFIG_NOSYSTEM=1
export GIT_CONFIG_GLOBAL=/dev/null
export GIT_AUTHOR_NAME=fixture GIT_AUTHOR_EMAIL=fixture@example.invalid
export GIT_COMMITTER_NAME=fixture GIT_COMMITTER_EMAIL=fixture@example.invalid
mkdir -p "$fixture_home/dev/work/worktrees/Assembly" "$fixture/bin" "$fixture/main/scripts" "$fixture/main/dotnet/Assembly.Desktop" "$fixture/main/desktop"
cp "$here/lane.sh" "$here/lane-env.sh" "$here/lane-reap.sh" "$here/dev-lane-clean.ts" "$here/dev-remote-identity.ts" "$here/dev-remote-process.ts" "$here/seed-dev.ts" "$fixture/main/scripts/"
chmod +x "$fixture/main/scripts/lane.sh" "$fixture/main/scripts/lane-reap.sh"
printf '<Project/>\n' > "$fixture/main/dotnet/Assembly.Desktop/Assembly.Desktop.csproj"
printf 'native fixture\n' > "$fixture/main/desktop/Cargo.toml"
printf '.lane\n' > "$fixture/main/.gitignore"

cat > "$fixture/bin/ssh" <<'SH'
#!/usr/bin/env bash
case "$*" in
  *'flock '*) echo '{"host":"fixture","reserved":true}' ;;
  *'cat ~/assembly-dotnet-*/lane-number'*) ;;
  *) ;;
esac
SH
chmod +x "$fixture/bin/ssh"
export PATH=$fixture/bin:$PATH

repo=$fixture/main
git init -q --bare "$fixture/origin"
git -C "$repo" init -q -b main
git -C "$repo" add .
git -C "$repo" commit -qm base
git -C "$repo" remote add origin "$fixture/origin"
git -C "$repo" push -qu origin main

make_lane() {
  local name=$1 num=$2
  local lane=$fixture_home/dev/work/worktrees/Assembly/$name
  local owner=11111111-1111-1111-1111-11111111111$num
  git -C "$repo" worktree add -q -b "lane/$name" "$lane" main
  printf 'LANE_NAME=%s\nLANE_NUMBER=%s\nLANE_OWNER=%s\n' "$name" "$num" "$owner" > "$lane/.lane"
  mkdir -p "$fixture_home/assembly-dev/lanes"
  node --input-type=module - "$fixture_home/assembly-dev/lanes/$name.json" "$lane" "$name" "$num" "$owner" <<'JS'
import fs from 'node:fs'; import {hostname} from 'node:os';
fs.writeFileSync(process.argv[2], JSON.stringify({version:1,name:process.argv[4],number:Number(process.argv[5]),owner:process.argv[6],host:hostname(),worktree:process.argv[3]}));
JS
}

# 1. Clean lane merged into origin/main (even when local main hasn't pulled yet) -> MUST reap.
make_lane merged-remote 1
merged_dir=$fixture_home/dev/work/worktrees/Assembly/merged-remote
printf ok > "$merged_dir/merged.txt"
git -C "$merged_dir" add merged.txt
git -C "$merged_dir" commit -qm merged
git -C "$merged_dir" push -q origin HEAD:refs/heads/main

# 2. Dirty merged lane -> MUST keep.
make_lane dirty-lane 2
dirty_dir=$fixture_home/dev/work/worktrees/Assembly/dirty-lane
printf dirty > "$dirty_dir/untracked.txt"

# 3. Unmerged clean lane -> MUST keep.
make_lane unmerged-lane 3
unmerged_dir=$fixture_home/dev/work/worktrees/Assembly/unmerged-lane
printf wip > "$unmerged_dir/wip.txt"
git -C "$unmerged_dir" add wip.txt
git -C "$unmerged_dir" commit -qm wip

# 4. Busy merged lane (live process in its cwd) -> MUST keep.
make_lane busy-lane 4
busy_dir=$fixture_home/dev/work/worktrees/Assembly/busy-lane
(cd "$busy_dir/desktop" && exec sleep 300) &
busy_pid=$!
children+=("$busy_pid")
deadline=$((SECONDS + 10))
while kill -0 "$busy_pid" 2>/dev/null; do
  files=$(lsof -a -p "$busy_pid" -d cwd -Fn) || files=
  if printf '%s\n' "$files" | grep -Fxq "n$busy_dir/desktop"; then break; fi
  [ "$SECONDS" -lt "$deadline" ] || { echo "FAIL: busy fixture never entered cwd" >&2; exit 1; }
  sleep 0.05
done

HOME="$fixture_home" bash "$repo/scripts/lane-reap.sh"

[ ! -d "$merged_dir" ] || { echo "FAIL: merged-remote lane was not reaped" >&2; exit 1; }
[ -d "$dirty_dir" ] || { echo "FAIL: dirty lane was unexpectedly removed" >&2; exit 1; }
[ -d "$unmerged_dir" ] || { echo "FAIL: unmerged lane was unexpectedly removed" >&2; exit 1; }
[ -d "$busy_dir" ] || { echo "FAIL: busy lane was unexpectedly removed" >&2; exit 1; }

echo "PASS lane-reap reaps clean merged lanes (including origin/main) and preserves dirty, unmerged, and busy lanes"
