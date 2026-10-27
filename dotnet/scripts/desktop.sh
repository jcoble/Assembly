#!/usr/bin/env bash
# Runs the desktop backend (dotnet/Assembly.Desktop) on 127.0.0.1:DESKTOP_PORT, from this checkout. The port and folders
# are this checkout's (scripts/lane-env.sh): main's are 18797, ~/assembly-dotnet and ~/.config/assembly-dotnet/env; a
# lane's carry its name and number. A checkout that isn't on main and has no .lane stops before it writes anything.
#   dotnet/scripts/desktop.sh serve          publishes Assembly.Desktop to DATA/mac/bin/<commit>, installs the adapters
#                                        under ~/assembly-dotnet/adapters (agents-file.sh), writes DATA/mac/agents.json,
#                                        makes the DATA/fixtures/notes-repo test repo if it's missing, then starts the
#                                        backend in the background; its log is DATA/mac/backend.log
#   dotnet/scripts/desktop.sh stop           stops it, by the pid in DATA/mac/backend.pid
#   AOT=1 dotnet/scripts/desktop.sh serve    the same, from a Native AOT build
#   dotnet/scripts/desktop.sh kill-adapter   the lost-run check: SIGKILLs the backend's adapter, found as its child
#                                        process by pid, never by name
# Every lane shares main's adapters install in ~/assembly-dotnet/adapters: a copy per lane would download them again.
# The adapters run with this computer's own Claude and Codex logins. The token is in TOKEN_FILE, made on first start.
set -euo pipefail
cd "$(dirname "$0")/../.."
. scripts/lane-env.sh
mode=${1:?usage: dotnet/scripts/desktop.sh serve|stop|kill-adapter}
home=$DATA/mac
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
notes=$DATA/fixtures/notes-repo
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
  dotnet publish -v q --nologo -c Release -r osx-arm64 -p:PublishAot=true -o "$bin" dotnet/Assembly.Desktop
else
  bin=$home/bin/$commit
  dotnet publish -v q --nologo -c Release -o "$bin" dotnet/Assembly.Desktop
fi
mkdir -p "$home"
nohup "$bin/Assembly.Desktop" serve --port "$DESKTOP_PORT" --home "$home" --token-file "$TOKEN_FILE" >> "$home/backend.log" 2>&1 &
echo $! > "$pidfile"
for _ in $(seq 50); do nc -z 127.0.0.1 "$DESKTOP_PORT" >/dev/null 2>&1 && break; sleep 0.2; done
nc -z 127.0.0.1 "$DESKTOP_PORT" >/dev/null 2>&1 || { echo "the backend didn't open $DESKTOP_PORT; see $home/backend.log" >&2; tail -5 "$home/backend.log" >&2; exit 1; }
echo "the backend (pid $(cat "$pidfile")) runs $commit on 127.0.0.1:$DESKTOP_PORT; log: $home/backend.log"
