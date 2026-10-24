#!/usr/bin/env bash
# Runs the backend on Workbox Test, from a git checkout on the Mac.
#   dotnet/scripts/workbox.sh smoke  [branch]   one smoke run; the transcript lands in dotnet/logs/
#   dotnet/scripts/workbox.sh record [branch]   every recorder scenario; the transcripts land in
#                                               dotnet/Assembly.Tests/Fixtures/acp/
#   dotnet/scripts/workbox.sh serve  [branch]   (re)starts the API on the box's 127.0.0.1:9797 as the
#                                               systemd user unit assembly-dotnet; the app's tunnel reaches it
#   dotnet/scripts/workbox.sh stop              stops that unit (systemd ends every process it started)
#   dotnet/scripts/workbox.sh kill-adapter      the lost-run check: SIGKILLs the unit's adapter, found as the
#                                               backend's child process by pid, never by name
#   dotnet/scripts/workbox.sh row-log <thread|latest> [--follow SECONDS]
#                                               a thread's rows by seq and by page, checked (row-log.mjs)
# Pushes the branch to the box's bare repo ~/assembly.git (never GitHub, same as scripts/deploy.sh),
# checks it out at ~/assembly/<branch>, installs the .NET 10 SDK under ~/.dotnet if it's missing, uses the
# adapters the TanStack AI spike installed under ~/spike-dotnet/adapters (no download; Assembly gets its own copy
# later), and runs with the box's
# own Claude and Codex logins. It touches nothing the tanstack-agent-server and tanstack-relay services use.
set -euo pipefail
cd "$(dirname "$0")/../.."
mode=${1:?usage: dotnet/scripts/workbox.sh smoke|record|serve|stop|kill-adapter|row-log [branch]}
branch=${2:-main}
case "$mode" in
  smoke|record|serve) ;;
  stop)
    ssh workbox-test 'systemctl --user stop assembly-dotnet && echo "assembly-dotnet stopped"'
    exit ;;
  kill-adapter)
    ssh workbox-test bash -s <<'BOX'
set -euo pipefail
main=$(systemctl --user show -p MainPID --value assembly-dotnet)
[ "$main" != 0 ] || { echo "assembly-dotnet isn't running" >&2; exit 1; }
children=$(ps -o pid= --ppid "$main" | xargs)
[ -n "$children" ] || { echo "the backend (pid $main) has no adapter running" >&2; exit 1; }
echo "killing the backend's adapter: pid $children (backend pid $main)"
kill -KILL $children
BOX
    exit ;;
  row-log)
    ssh workbox-test "PATH=\$HOME/.local/bin:\$PATH node --input-type=module - ${*:2}" < dotnet/scripts/row-log.mjs
    exit ;;
  *) echo "unknown mode: $mode" >&2; exit 2 ;;
esac
commit=$(git rev-parse --verify "$branch^{commit}")

ssh workbox-test 'test -d ~/assembly.git || git init -q --bare ~/assembly.git'
git push -q --force ssh://workbox-test/~/assembly.git "$commit:refs/heads/$branch"

result=$(ssh workbox-test bash -s -- "$mode" "$branch" "$commit" <<'BOX'
set -euo pipefail
mode=$1 branch=$2 commit=$3
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
mkdir -p ~/assembly-dotnet/serve
"$dir/dotnet/scripts/agents-file.sh" "$adapters" ~/assembly-dotnet/serve/agents.json

if [ "$mode" = serve ]; then
  # Published apart from the checkout, so a later smoke build never swaps files under the running server.
  bin=~/assembly-dotnet/bin/$commit
  dotnet publish -v q --nologo -c Release -o "$bin" "$dir/dotnet/Assembly.Remote" >&2
  ln -sfn "$bin" ~/assembly-dotnet/bin/current
  mkdir -p ~/.config/systemd/user
  cat > ~/.config/systemd/user/assembly-dotnet.service <<UNIT
[Unit]
Description=.NET remote host for Assembly (dotnet/Assembly.Remote)

[Service]
Environment=PATH=%h/.dotnet:%h/.local/bin:/usr/local/bin:/usr/bin:/bin
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=%h/.dotnet/dotnet %h/assembly-dotnet/bin/current/Assembly.Remote.dll serve --port 9797
UNIT
  systemctl --user daemon-reload
  systemctl --user restart assembly-dotnet
  sleep 3
  systemctl --user is-active assembly-dotnet >&2
  journalctl --user -u assembly-dotnet -n 5 --no-pager -o cat >&2
  echo "serve 0"
  exit
fi

dotnet build -v q --nologo "$dir/dotnet/Assembly.Remote" >&2
backend="$dir/dotnet/Assembly.Remote/bin/Debug/net10.0/Assembly.Remote.dll"
stamp=$(date -u +%Y%m%d-%H%M%S)
# The run's exit code is the result; report it after the output path.
set +e
if [ "$mode" = smoke ]; then
  mkdir -p ~/assembly-dotnet/logs
  out=~/assembly-dotnet/logs/smoke-$stamp.jsonl
  cwd=$(mktemp -d ~/assembly-dotnet/smoke-cwd-XXXXXX)
  dotnet "$backend" smoke --cwd "$cwd" --log "$out" -- node "$entry" >&2
else
  out=~/assembly-dotnet/fixtures-$stamp
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
  echo "assembly-dotnet runs $commit on Workbox Test at 127.0.0.1:9797; its token is in ~/.config/assembly-dotnet/env there"
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
