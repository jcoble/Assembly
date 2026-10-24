#!/usr/bin/env bash
# Builds and runs Assembly: the shell (shell/) with the .NET backend (dotnet/Assembly.Mac) bundled inside it.
#   shell/assembly.sh build   publishes Assembly.Mac (Native AOT) to shell/backend, builds app/ and the shell, and
#                             puts the bundle at ~/assembly-dotnet/mac/Assembly.app
#   shell/assembly.sh start   writes the agents file and opens the bundle with `open`, so WebKit's processes are
#                             attributed to it; ASSEMBLY_REMOTE=workbox-test adds that machine, ASSEMBLY_SOAK sets the
#                             page's query. The backend's log is ~/assembly-dotnet/mac/backend.log
#   shell/assembly.sh stop    quits it as Cmd-Q does, so the shell stops the backend; waits for the pid in
#                             ~/assembly-dotnet/mac/app.pid to exit
# ADAPTERS is where the ACP adapters are installed (default ~/assembly-dotnet/adapters; agents-file.sh installs
# them there if they're missing).
set -euo pipefail
cd "$(dirname "$0")/.."
mode=${1:?usage: shell/assembly.sh build|start|stop}
home=~/assembly-dotnet/mac
bundle="$home/Assembly.app"
pidfile=$home/app.pid
running() { [ -f "$pidfile" ] && kill -0 "$(cat "$pidfile")" 2>/dev/null; }

case "$mode" in
  build)
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
    rm -rf shell/backend
    dotnet publish -v q --nologo -c Release -r osx-arm64 -p:PublishAot=true -o shell/backend dotnet/Assembly.Mac
    rm -f shell/backend/*.dbg shell/backend/*.dSYM 2>/dev/null || true
    (cd shell && pnpm install --silent && pnpm tauri build --bundles app >/dev/null)
    mkdir -p "$home"
    rm -rf "$bundle"
    cp -R shell/target/release/bundle/macos/Assembly.app "$bundle"
    echo "built $bundle from $(git rev-parse --short HEAD)" ;;
  start)
    if running; then echo "Assembly is already running (pid $(cat "$pidfile"))" >&2; exit 1; fi
    [ -d "$bundle" ] || { echo "no bundle yet; run shell/assembly.sh build" >&2; exit 1; }
    mkdir -p "$home"
    dotnet/scripts/agents-file.sh "${ADAPTERS:-$HOME/assembly-dotnet/adapters}" "$home/agents.json"
    envs=(--env "PATH=$PATH")
    [ -n "${ASSEMBLY_REMOTE:-}" ] && envs+=(--env "ASSEMBLY_REMOTE=$ASSEMBLY_REMOTE")
    [ -n "${ASSEMBLY_SOAK:-}" ] && envs+=(--env "ASSEMBLY_SOAK=$ASSEMBLY_SOAK")
    open -n "${envs[@]}" "$bundle"
    for _ in $(seq 100); do nc -z 127.0.0.1 18797 >/dev/null 2>&1 && break; sleep 0.2; done
    pid=$(pgrep -nf "$bundle/Contents/MacOS/assembly-shell" || true)
    [ -n "$pid" ] || { echo "Assembly didn't start; see $home/backend.log" >&2; exit 1; }
    echo "$pid" > "$pidfile"
    echo "Assembly (pid $pid) runs; its backend is on 127.0.0.1:18797; log: $home/backend.log" ;;
  stop)
    running || { echo "Assembly isn't running" >&2; exit 1; }
    pid=$(cat "$pidfile")
    # A plain SIGTERM would end the shell before it stops the backend.
    osascript -e 'tell application id "dev.jcoble.assembly" to quit'
    for _ in $(seq 150); do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
    if kill -0 "$pid" 2>/dev/null; then echo "Assembly (pid $pid) is still running after 30 s" >&2; exit 1; fi
    rm -f "$pidfile"
    echo "Assembly (pid $pid) stopped" ;;
  *) echo "unknown mode: $mode" >&2; exit 2 ;;
esac
