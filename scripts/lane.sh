#!/usr/bin/env bash
# Lane worktrees: a checkout of its own under ~/dev/work/worktrees/Assembly/<name>, on branch lane/<name>, with a
# .lane file that gives it its own ports, folders, app id and Workbox Test host (scripts/lane-env.sh). Works on the
# Mac and on the workbox; a lane's number (1 to 9) is reserved on Workbox Test, which every lane's host shares.
#   scripts/lane.sh new <name>      makes the worktree from main and installs app/'s packages (and shell/'s on the Mac)
#   scripts/lane.sh adopt <name>    makes a worktree here for a lane made elsewhere (its branch lane/<name> pushed
#                                   to GitHub), with the same number, so its ports, folders, app id and Workbox Test
#                                   host match: the Mac adopts a workbox lane for the real-window checks
#   scripts/lane.sh remove <name>   stops its window (Mac), removes its Workbox Test host and folders (unless the
#                                   lane was adopted here: the machine that made it removes those), then the
#                                   worktree; refuses a worktree with uncommitted changes. The branch stays unless merged
#   scripts/lane.sh list            the lanes here and the numbers reserved on Workbox Test
set -euo pipefail
cd "$(dirname "$0")/.."
mode=${1:?usage: scripts/lane.sh new|remove|list [name]}
name=${2:-}
lanes=$HOME/dev/work/worktrees/Assembly
dir=$lanes/$name
main=$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")
reserved() { ssh workbox-test 'cat ~/assembly-dotnet-*/lane-number 2>/dev/null' || true; }

case "$mode" in
  new)
    [[ $name =~ ^[a-z0-9][a-z0-9-]*$ ]] || { echo "a lane name is lowercase letters, digits and dashes" >&2; exit 2; }
    [ ! -e "$dir" ] || { echo "$dir already exists" >&2; exit 1; }
    used=$(reserved)
    number=""
    for n in $(seq 9); do grep -qx "$n" <<<"$used" || { number=$n; break; }; done
    [ -n "$number" ] || { echo "all 9 lane numbers are taken on Workbox Test; remove a lane first" >&2; exit 1; }
    ssh workbox-test "mkdir -p ~/assembly-dotnet-$name && echo $number > ~/assembly-dotnet-$name/lane-number"
    git -C "$main" worktree add -q -b "lane/$name" "$dir" main
    git -C "$dir" config core.hooksPath .githooks
    printf 'LANE_NAME=%s\nLANE_NUMBER=%s\n' "$name" "$number" > "$dir/.lane"
    (cd "$dir/app" && pnpm install --silent --prefer-offline)
    [ "$(uname)" = Darwin ] && (cd "$dir/shell" && pnpm install --silent --prefer-offline)
    (cd "$dir" && . scripts/lane-env.sh && echo "lane $name (number $number) is at $dir on lane/$name:
  Desktop backend 127.0.0.1:$DESKTOP_PORT, vite dev localhost:$DEV_PORT, data $DATA
  Workbox Test host 127.0.0.1:$BOX_PORT, unit $BOX_UNIT, folder ~/$BOX_DATA") ;;
  adopt)
    [ -n "$name" ] || { echo "usage: scripts/lane.sh adopt <name>" >&2; exit 2; }
    [ ! -e "$dir" ] || { echo "$dir already exists" >&2; exit 1; }
    number=$(ssh workbox-test "cat ~/assembly-dotnet-$name/lane-number 2>/dev/null" || true)
    [ -n "$number" ] || { echo "no lane named '$name' is reserved on Workbox Test" >&2; exit 1; }
    git -C "$main" fetch -q origin "lane/$name"
    git -C "$main" worktree add -q -B "lane/$name" "$dir" FETCH_HEAD
    git -C "$dir" branch -q --set-upstream-to="origin/lane/$name"
    git -C "$dir" config core.hooksPath .githooks
    printf 'LANE_NAME=%s\nLANE_NUMBER=%s\nLANE_ADOPTED=1\n' "$name" "$number" > "$dir/.lane"
    (cd "$dir/app" && pnpm install --silent --prefer-offline)
    [ "$(uname)" = Darwin ] && (cd "$dir/shell" && pnpm install --silent --prefer-offline)
    echo "adopted lane $name (number $number) at $dir on lane/$name" ;;
  remove)
    [ -n "$name" ] && [ -f "$dir/.lane" ] || { echo "no lane named '$name' at $dir" >&2; exit 1; }
    dirty=$(git -C "$dir" status --short)
    [ -z "$dirty" ] || { echo "$dir has uncommitted changes; commit or stash them first:"; echo "$dirty"; exit 1; } >&2
    if [ "$(uname)" = Darwin ]; then
      (cd "$dir" && . scripts/lane-env.sh && [ -f "$DATA/mac/app.pid" ] && kill -0 "$(cat "$DATA/mac/app.pid")" 2>/dev/null) \
        && "$dir/shell/assembly.sh" stop
    fi
    if grep -q '^LANE_ADOPTED=1' "$dir/.lane"; then
      echo "lane $name was adopted here; its Workbox Test host stays for the machine that made it to remove"
    else
      "$dir/dotnet/scripts/workbox.sh" remove "lane/$name"
    fi
    git -C "$main" worktree remove "$dir"
    git -C "$main" worktree prune
    git -C "$main" branch -d "lane/$name" 2>/dev/null && echo "deleted the local branch lane/$name (merged, or all of it pushed)" || echo "kept branch lane/$name (it has commits that aren't merged or pushed)"
    echo "removed lane $name; its desktop data folder, if any, stays at ~/assembly-dotnet-$name" ;;
  list)
    git -C "$main" worktree list | grep "$lanes/" || echo "no lanes here"
    echo "numbers reserved on Workbox Test:"
    ssh workbox-test 'for f in ~/assembly-dotnet-*/lane-number; do [ -f "$f" ] && echo "  $(cat "$f") $(basename "$(dirname "$f")")"; done; true' ;;
  *) echo "unknown mode: $mode" >&2; exit 2 ;;
esac
