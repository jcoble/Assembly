#!/usr/bin/env bash
# Lane worktrees: a checkout of its own under ~/dev/work/worktrees/Assembly/<name>, on branch lane/<name>, with a
# .lane file that gives it its own ports, folders, app id and Workbox Test host (scripts/lane-env.sh). Works on the
# Mac and on the workbox; a lane's number (1 to 9) is reserved on Workbox Test, which every lane's host shares.
#   scripts/lane.sh new <name>      makes the worktree from main
#   scripts/lane.sh adopt <name>    makes a worktree here for a lane made elsewhere (its branch lane/<name> pushed
#                                   to GitHub), with the same number, so its ports, folders, app id and Workbox Test
#                                   host match: the Mac adopts a workbox lane for the real-window checks
#   scripts/lane.sh remove <name>   removes its Workbox Test host and folders (unless the
#                                   lane was adopted here: the machine that made it removes those), then the
#                                   worktree; refuses dirty/unmerged work or running lane processes. Close the native app first
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
    (cd "$dir" && . scripts/lane-env.sh && echo "lane $name (number $number) is at $dir on lane/$name:
  Desktop backend 127.0.0.1:$DESKTOP_PORT, data $DATA
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
    echo "adopted lane $name (number $number) at $dir on lane/$name" ;;
  remove)
    [ -n "$name" ] && [ -f "$dir/.lane" ] || { echo "no lane named '$name' at $dir" >&2; exit 1; }
    dirty=$(git -C "$dir" status --short)
    [ -z "$dirty" ] || { echo "$dir has uncommitted changes; commit or stash them first:"; echo "$dirty"; exit 1; } >&2
    git -C "$dir" merge-base --is-ancestor HEAD main || {
      echo "$dir has work not merged into main; merge it before removing the lane" >&2; exit 1;
    }
    # Both app:dev (desktop/) and ui-check (checkout root) retain a lane cwd.
    # Inspect actual open directories, including when no legacy app.pid exists.
    # A missing/failed ownership inspection must never permit destructive cleanup.
    command -v lsof >/dev/null || { echo "cannot prove lane processes are stopped: lsof is required" >&2; exit 1; }
    owned=$(lsof -a -u "$(id -u)" -d cwd -Fn) || {
      echo "cannot inspect lane process ownership; close the native app and retry" >&2; exit 1;
    }
    (cd "$dir" && . scripts/lane-env.sh
      if printf '%s\n' "$owned" | awk -v lane="$dir" -v data="$DATA" '
        /^n/ { path=substr($0,2); if (path==lane || index(path,lane "/")==1 || path==data || index(path,data "/")==1) found=1 }
        END { exit !found }'; then
        echo "lane $name has a live owned process; close the native app (or stop its ui-check session) before removing it" >&2
        exit 1
      fi
      # ui-check also records backend/Xvfb IDs. Inspect those actual processes;
      # never signal an ID or trust a stale/reused legacy app.pid.
      if [ -f "$DATA/ui-check/state" ]; then
        while IFS='=' read -r key pid; do
          case "$key" in app|backend|xvfb) ;; *) continue ;; esac
          [[ $pid =~ ^[1-9][0-9]*$ ]] || { echo "cannot inspect invalid ui-check $key PID" >&2; exit 1; }
          if kill -0 "$pid" 2>/dev/null; then
            files=$(lsof -a -p "$pid" -d cwd,txt -Fn) || {
              echo "cannot inspect ui-check $key ownership; close the native app and retry" >&2; exit 1;
            }
            if printf '%s\n' "$files" | awk -v lane="$dir" -v data="$DATA" '
              /^n/ { path=substr($0,2); if (path==lane || index(path,lane "/")==1 || path==data || index(path,data "/")==1) found=1 }
              END { exit !found }'; then
              echo "lane $name has a live owned ui-check $key; close the native app (or stop its ui-check session) before removing it" >&2
              exit 1
            fi
          fi
        done < "$DATA/ui-check/state"
      fi
    )
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
