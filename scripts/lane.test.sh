#!/usr/bin/env bash
# Behavioral lane lifecycle checks in disposable repositories and HOME; no native build or remote access.
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
export CALLS=$fixture/calls
mkdir -p "$fixture_home/dev/work/worktrees/Assembly" "$fixture/bin" "$fixture/main/scripts" "$fixture/main/dotnet/scripts" "$fixture/main/desktop"
cp "$here/lane.sh" "$here/lane-env.sh" "$fixture/main/scripts/"
printf '.lane\n' > "$fixture/main/.gitignore"
printf 'native fixture\n' > "$fixture/main/desktop/marker"
cat > "$fixture/main/dotnet/scripts/workbox.sh" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CALLS"
SH
chmod +x "$fixture/main/dotnet/scripts/workbox.sh"
cat > "$fixture/bin/ssh" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CALLS"
# Lane 1 is already reserved; new lanes must use 2.
case "$*" in *'cat ~/assembly-dotnet-*/lane-number'*) echo 1 ;; esac
SH
chmod +x "$fixture/bin/ssh"
export PATH=$fixture/bin:$PATH
repo=$fixture/main
lane=$fixture_home/dev/work/worktrees/Assembly/example
git -C "$repo" init -q -b main
git -C "$repo" add .
git -C "$repo" commit -qm base
run_lane() { HOME="$fixture_home" bash "$repo/scripts/lane.sh" "$@"; }
# lsof exposes both cwd and executable text paths on macOS and Linux.
# Observe only the saved child PID, with a deadline if chdir/exec never completes.
wait_fixture_path() {
  local pid=$1 descriptor=$2 expected=$3 deadline=$((SECONDS + 10)) files
  while kill -0 "$pid" 2>/dev/null; do
    files=$(lsof -a -p "$pid" -d "$descriptor" -Fn) || files=
    if printf '%s\n' "$files" | grep -Fxq "n$expected"; then return 0; fi
    [ "$SECONDS" -lt "$deadline" ] || break
    sleep 0.05
  done
  echo "FAIL: fixture PID $pid never exposed $descriptor path $expected" >&2
  return 1
}
make_lane() {
  git -C "$repo" worktree add -q -b lane/example "$lane" main
  printf 'LANE_NAME=example\nLANE_NUMBER=2\n' > "$lane/.lane"
  : > "$CALLS"
}
refused() {
  local reason=$1 out
  if out=$(run_lane remove example 2>&1); then echo "FAIL: removal succeeded: $reason" >&2; exit 1; fi
  [[ $out == *"$reason"* ]] || { echo "FAIL: expected '$reason': $out" >&2; exit 1; }
  [ -d "$lane" ] && [ ! -s "$CALLS" ] || { echo 'FAIL: refusal mutated lane/remote' >&2; exit 1; }
}
removed() {
  run_lane remove example > "$fixture/output" 2>&1
  [ ! -d "$lane" ] || { echo 'FAIL: worktree remains' >&2; exit 1; }
  if git -C "$repo" show-ref --verify --quiet refs/heads/lane/example; then
    echo 'FAIL: merged branch remains' >&2; exit 1
  fi
}
make_lane
printf dirty > "$lane/dirty"
refused 'uncommitted changes'
rm "$lane/dirty"
echo 'PASS dirty work refuses before cleanup'
printf unmerged > "$lane/change"
git -C "$lane" add change
git -C "$lane" commit -qm change
refused 'not merged into main'
# Even a fully pushed branch is not safely merged.
git init -q --bare "$fixture/origin"
git -C "$repo" remote add origin "$fixture/origin"
git -C "$lane" push -qu origin lane/example
refused 'not merged into main'
echo 'PASS unmerged and pushed-unmerged work refuses before cleanup'
git -C "$repo" merge -q --ff-only lane/example
# Use the actual supported app:dev cwd without any app.pid or native process.
(cd "$lane/desktop" && exec sleep 300) &
app=$!
children+=("$app")
# The fixture child inherits this directory before exec; wait for that observable cwd.
wait_fixture_path "$app" cwd "$lane/desktop"
refused 'close the native app'
kill "$app"; wait "$app" 2>/dev/null || true
children=()
echo 'PASS live native-launch cwd without app.pid refuses'
# A recorded ui-check backend remains identifiable by its executable even with another cwd.
data=$fixture_home/assembly-dotnet-example
mkdir -p "$data/ui-check/backend"
cp "$(command -v sleep)" "$data/ui-check/backend/Assembly.Desktop"
(cd "$fixture" && exec "$data/ui-check/backend/Assembly.Desktop" 300) &
backend=$!
children+=("$backend")
wait_fixture_path "$backend" txt "$data/ui-check/backend/Assembly.Desktop"
printf 'backend=%s\n' "$backend" > "$data/ui-check/state"
refused 'live owned ui-check backend'
kill "$backend"; wait "$backend" 2>/dev/null || true
children=()
rm "$data/ui-check/state"
echo 'PASS recorded ui-check backend ownership refuses'
cat > "$fixture/bin/lsof" <<'SH'
#!/usr/bin/env bash
exit 1
SH
chmod +x "$fixture/bin/lsof"
refused 'cannot inspect lane process ownership'
rm "$fixture/bin/lsof"
echo 'PASS failed ownership inspection refuses'
# A reused legacy PID and an unrelated reused ui-check ID are never signalled or treated as owned.
(cd "$fixture" && exec sleep 300) &
unrelated=$!
children+=("$unrelated")
wait_fixture_path "$unrelated" cwd "$fixture"
mkdir -p "$data/mac"
printf '%s\n' "$unrelated" > "$data/mac/app.pid"
printf 'app=%s\n' "$unrelated" > "$data/ui-check/state"
removed
kill -0 "$unrelated"
[ "$(cat "$CALLS")" = 'remove lane/example' ]
[ -f "$data/mac/app.pid" ]
echo 'PASS merged removal preserves unrelated PID and desktop data; cleans remote once'
make_lane
printf 'LANE_ADOPTED=1\n' >> "$lane/.lane"
removed
[ ! -s "$CALLS" ]
echo 'PASS adopted removal preserves remote reservation'
# New checkout succeeds with only native roots, keeps reserved numbering/ports and needs no pnpm installs.
run_lane new fresh > "$fixture/output" 2>&1
fresh=$fixture_home/dev/work/worktrees/Assembly/fresh
[ "$(cat "$fresh/.lane")" = $'LANE_NAME=fresh\nLANE_NUMBER=2' ]
values=$(cd "$fresh" && HOME="$fixture_home" bash -ec '. scripts/lane-env.sh; echo "$DESKTOP_PORT $BOX_PORT $BOX_UNIT $DATA"')
[ "$values" = "18817 9817 assembly-dotnet-fresh $fixture_home/assembly-dotnet-fresh" ]
[ "$(git -C "$fresh" rev-parse HEAD)" = "$(git -C "$repo" rev-parse main)" ]
echo 'PASS new native-only lane preserves main base, reserved numbering and isolation'
