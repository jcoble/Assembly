#!/usr/bin/env bash
# Runs the Mac's backend (dotnet/Assembly.Mac) on 127.0.0.1:18797, from this checkout.
#   dotnet/scripts/mac.sh serve          publishes Assembly.Mac to ~/assembly-dotnet/mac/bin/<commit>, installs the
#                                        adapters under ~/assembly-dotnet/adapters (agents-file.sh) and makes the
#                                        ~/assembly-dotnet/fixtures/notes-repo test repo if they're missing, then
#                                        starts the backend in the background; its log is ~/assembly-dotnet/mac/backend.log
#   dotnet/scripts/mac.sh stop           stops it, by the pid in ~/assembly-dotnet/mac/backend.pid
#   AOT=1 dotnet/scripts/mac.sh serve    the same, from a Native AOT build
#   dotnet/scripts/mac.sh kill-adapter   the lost-run check: SIGKILLs the backend's adapter, found as its child
#                                        process by pid, never by name
# The adapters run with this Mac's own Claude and Codex logins. The token is in ~/.config/assembly-dotnet/env, made on first start.
set -euo pipefail
cd "$(dirname "$0")/../.."
mode=${1:?usage: dotnet/scripts/mac.sh serve|stop|kill-adapter}
home=~/assembly-dotnet/mac
pidfile=$home/backend.pid
running() { [ -f "$pidfile" ] && kill -0 "$(cat "$pidfile")" 2>/dev/null; }

case "$mode" in
  serve) ;;
  stop)
    running || { echo "the backend isn't running" >&2; exit 1; }
    pid=$(cat "$pidfile")
    kill "$pid"
    for _ in $(seq 150); do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
    if kill -0 "$pid" 2>/dev/null; then echo "the backend (pid $pid) is still running after 30 s" >&2; exit 1; fi
    rm -f "$pidfile"
    echo "the backend (pid $pid) stopped"
    exit ;;
  kill-adapter)
    running || { echo "the backend isn't running" >&2; exit 1; }
    pid=$(cat "$pidfile")
    children=$(pgrep -P "$pid" | xargs || true)
    [ -n "$children" ] || { echo "the backend (pid $pid) has no adapter running" >&2; exit 1; }
    echo "killing the backend's adapter: pid $children (backend pid $pid)"
    kill -KILL $children
    exit ;;
  *) echo "unknown mode: $mode" >&2; exit 2 ;;
esac

if running; then echo "the backend is already running (pid $(cat "$pidfile")); stop it first" >&2; exit 1; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1

mkdir -p "$home"
dotnet/scripts/agents-file.sh ~/assembly-dotnet/adapters "$home/agents.json"

# The checks' test repo: three lines the agent can read and edit.
notes=~/assembly-dotnet/fixtures/notes-repo
if [ ! -d "$notes/.git" ]; then
  mkdir -p "$notes"
  printf 'alpha\nbeta\ngamma\n' > "$notes/notes.txt"
  git -C "$notes" init -q
  git -C "$notes" add notes.txt
  git -C "$notes" -c user.name=assembly -c user.email=assembly@localhost commit -q -m "notes"
fi

# Published apart from the checkout, so a later build never swaps files under the running backend.
# AOT=1 serves a Native AOT build instead of the JIT one.
commit=$(git rev-parse HEAD)
if [ "${AOT:-}" = 1 ]; then
  bin=$home/bin-aot/$commit
  dotnet publish -v q --nologo -c Release -r osx-arm64 -p:PublishAot=true -o "$bin" dotnet/Assembly.Mac
else
  bin=$home/bin/$commit
  dotnet publish -v q --nologo -c Release -o "$bin" dotnet/Assembly.Mac
fi
mkdir -p "$home"
nohup "$bin/Assembly.Mac" serve --port 18797 --home "$home" >> "$home/backend.log" 2>&1 &
echo $! > "$pidfile"
for _ in $(seq 50); do nc -z 127.0.0.1 18797 >/dev/null 2>&1 && break; sleep 0.2; done
nc -z 127.0.0.1 18797 >/dev/null 2>&1 || { echo "the backend didn't open 18797; see $home/backend.log" >&2; tail -5 "$home/backend.log" >&2; exit 1; }
echo "the backend (pid $(cat "$pidfile")) runs $commit on 127.0.0.1:18797; log: $home/backend.log"
