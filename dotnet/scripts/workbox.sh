#!/usr/bin/env bash
# Runs the backend on Workbox Test, from a git checkout on the Mac or the workbox.
#   dotnet/scripts/workbox.sh smoke  [branch]   one smoke run; the transcript lands in dotnet/logs/
#   dotnet/scripts/workbox.sh record [branch]   every recorder scenario; the transcripts land in
#                                               dotnet/Assembly.Tests/Fixtures/acp/
#   dotnet/scripts/workbox.sh serve  [branch]   (re)starts the API on the box's 127.0.0.1:BOX_PORT as the
#                                               systemd user unit BOX_UNIT; the app's tunnel reaches it
#   dotnet/scripts/workbox.sh stop              stops that unit (systemd ends every process it started)
#   dotnet/scripts/workbox.sh remove            stops that unit and removes it, its folders and its checkout
# The port, unit and folders are this checkout's (scripts/lane-env.sh): main's are 9797, assembly-dotnet and
# ~/assembly-dotnet; a lane's carry its name. The branch defaults to this checkout's.
#   dotnet/scripts/workbox.sh kill-adapter      the lost-run check: SIGKILLs the unit's adapter, found as the
#                                               backend's child process by pid, never by name
#   dotnet/scripts/workbox.sh row-log <session|latest> [--follow SECONDS]
#                                               a session's rows by seq and by page, checked (row-log.ts)
# Pushes the branch to the box's bare repo ~/assembly.git (never GitHub, same as scripts/deploy.sh),
# checks it out at ~/assembly/<branch>, installs the .NET 10 SDK under ~/.dotnet if it's missing, uses the
# adapters the TanStack AI spike installed under ~/spike-dotnet/adapters (no download; Assembly gets its own copy
# later), and runs with the box's
# own Claude and Codex logins. It touches nothing the tanstack-agent-server and tanstack-relay services use.
set -euo pipefail
cd "$(dirname "$0")/../.."
. scripts/lane-env.sh
mode=${1:?usage: dotnet/scripts/workbox.sh smoke|record|serve|stop|remove|kill-adapter|row-log [branch]}
branch=${2:-$(git branch --show-current)}
echo "lane ${LANE_NAME:-main}: box port $BOX_PORT, unit $BOX_UNIT, box data ~/$BOX_DATA" >&2
case "$mode" in
  smoke|record|serve) ;;
  stop)
    ssh workbox-test "systemctl --user stop $BOX_UNIT && echo '$BOX_UNIT stopped'"
    exit ;;
  remove)
    [ -n "$LANE_NAME" ] || { echo "remove is for a lane's host; main's stays" >&2; exit 1; }
    ssh workbox-test bash -s -- "$BOX_UNIT" "$BOX_DATA" "$BOX_TOKEN_FILE" "$branch" <<'BOX'
set -euo pipefail
unit=$1 data=$2 token=$3 branch=$4
systemctl --user stop "$unit" 2>/dev/null || true
rm -f ~/.config/systemd/user/"$unit".service
systemctl --user daemon-reload
rm -rf ~/"$data" ~/"$(dirname "$token")"
if [ -d ~/assembly/"$branch" ]; then git -C ~/assembly.git worktree remove --force ~/assembly/"$branch"; fi
git -C ~/assembly.git worktree prune
git -C ~/assembly.git branch -D "$branch" >/dev/null 2>&1 || true
echo "removed $unit, ~/$data and ~/assembly/$branch"
BOX
    exit ;;
  kill-adapter)
    [ -n "$LANE_NAME" ] || { echo "kill-adapter is for a lane's host; main's host keeps its adapter" >&2; exit 1; }
    ssh workbox-test bash -s -- "$BOX_UNIT" <<'BOX'
set -euo pipefail
unit=$1
main=$(systemctl --user show -p MainPID --value "$unit")
[ "$main" != 0 ] || { echo "$unit isn't running" >&2; exit 1; }
children=$(ps -o pid= --ppid "$main" | xargs)
[ -n "$children" ] || { echo "the backend (pid $main) has no adapter running" >&2; exit 1; }
echo "killing the backend's adapter: pid $children (backend pid $main)"
kill -KILL $children
BOX
    exit ;;
  row-log)
    ssh workbox-test "PATH=\$HOME/.local/bin:\$PATH node --input-type=module-typescript - ${*:2}" < dotnet/scripts/row-log.ts
    exit ;;
  *) echo "unknown mode: $mode" >&2; exit 2 ;;
esac
commit=$(git rev-parse --verify "$branch^{commit}")
# Main's host runs only what's on main: a clean checkout of origin/main as it is now. Main's host is anything that
# resolves to its unit, port or data, whatever the branch or .lane.
if [ "$mode" = serve ] && { [ "$BOX_UNIT" = assembly-dotnet ] || [ "$BOX_PORT" = 9797 ] || [ "$BOX_DATA" = assembly-dotnet ]; }; then
  git fetch -q origin main
  main=$(git rev-parse origin/main)
  if [ "$commit" != "$main" ] || [ "$(git rev-parse HEAD)" != "$main" ] || [ -n "$(git status --porcelain)" ]; then
    echo "stop: $BOX_UNIT is main's host and runs only a clean checkout of origin/main ($main); this is $commit" >&2
    exit 1
  fi
fi

ssh workbox-test 'test -d ~/assembly.git || git init -q --bare ~/assembly.git'
git push -q --force ssh://workbox-test/~/assembly.git "$commit:refs/heads/$branch"

result=$(ssh workbox-test bash -s -- "$mode" "$branch" "$commit" "$BOX_UNIT" "$BOX_DATA" "$BOX_PORT" "$BOX_TOKEN_FILE" <<'BOX'
set -euo pipefail
mode=$1 branch=$2 commit=$3 unit=$4 data=$5 port=$6 token=$7
export PATH="$HOME/.dotnet:$HOME/.local/bin:/usr/local/bin:/usr/bin:/bin"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dir=~/assembly/$branch
if [ -d "$dir" ]; then
  git -C "$dir" checkout -q --detach "$commit"
else
  git -C ~/assembly.git worktree add -q --detach "$dir" "$commit"
fi

if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install-$$.sh
  bash /tmp/dotnet-install-$$.sh --channel 10.0 --install-dir ~/.dotnet >&2
  rm -f /tmp/dotnet-install-$$.sh
fi

adapters=~/spike-dotnet/adapters
entry=$adapters/node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js
# The box has pnpm but no npm.

if [ "$mode" = serve ]; then
  # Only the server reads agents.json; smoke and record start the adapter themselves.
  mkdir -p ~/"$data"/serve
  "$dir/dotnet/scripts/agents-file.sh" "$adapters" ~/"$data"/serve/agents.json
  # Published apart from the checkout, so a later smoke build never swaps files under the running server.
  bin=~/$data/bin/$commit
  dotnet publish -v q --nologo -c Release -o "$bin" "$dir/dotnet/Assembly.Remote" >&2
  ln -sfn "$bin" ~/"$data"/bin/current
  mkdir -p ~/.config/systemd/user
  cat > ~/.config/systemd/user/"$unit".service <<UNIT
[Unit]
Description=.NET remote host for Assembly (dotnet/Assembly.Remote)

[Service]
Environment=PATH=%h/.dotnet:%h/.local/bin:/usr/local/bin:/usr/bin:/bin
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=%h/.dotnet/dotnet %h/$data/bin/current/Assembly.Remote.dll serve --port $port --home %h/$data/serve --token-file %h/$token
UNIT
  systemctl --user daemon-reload
  systemctl --user restart "$unit"
  sleep 3
  systemctl --user is-active "$unit" >&2
  journalctl --user -u "$unit" -n 5 --no-pager -o cat >&2
  echo "serve 0"
  exit
fi

dotnet build -v q --nologo "$dir/dotnet/Assembly.Remote" >&2
backend="$dir/dotnet/Assembly.Remote/bin/Debug/net10.0/Assembly.Remote.dll"
stamp=$(date -u +%Y%m%d-%H%M%S)
# The run's exit code is the result; report it after the output path.
set +e
if [ "$mode" = smoke ]; then
  mkdir -p ~/"$data"/logs
  out=~/"$data"/logs/smoke-$stamp.jsonl
  cwd=$(mktemp -d ~/"$data"/smoke-cwd-XXXXXX)
  dotnet "$backend" smoke --cwd "$cwd" --log "$out" -- node "$entry" >&2
else
  out=~/"$data"/fixtures-$stamp
  dotnet "$backend" record --out "$out" -- node "$entry" >&2
fi
code=$?
set -e
echo "adapter processes still running afterwards:" >&2
pgrep -af "$entry" >&2 || echo "  none" >&2
echo "$out $code"
BOX
)
read -r remote_out code <<<"$result"
if [ "$mode" = serve ]; then
  echo "$BOX_UNIT runs $commit on Workbox Test at 127.0.0.1:$BOX_PORT; its token is in ~/$BOX_TOKEN_FILE there"
elif [ "$mode" = smoke ]; then
  mkdir -p dotnet/logs
  scp -q "workbox-test:$remote_out" dotnet/logs/
  echo "transcript: dotnet/logs/$(basename "$remote_out")"
else
  fixtures=dotnet/Assembly.Tests/Fixtures/acp
  mkdir -p "$fixtures"
  scp -q "workbox-test:$remote_out/*.jsonl" "$fixtures/"
  echo "fixtures: $fixtures"
fi
exit "$code"
