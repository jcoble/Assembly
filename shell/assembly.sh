#!/usr/bin/env bash
# Builds and runs Assembly from this checkout: the shell (shell/) with the .NET backend (dotnet/Assembly.Desktop)
# bundled inside it, on this checkout's ports and folders (scripts/lane-env.sh).
#   shell/assembly.sh build             publishes Assembly.Desktop (Native AOT) to shell/backend with the lane's
#                                       values (backend/lane.json), builds app/ and the shell, and puts the
#                                       bundle at DATA/mac/APP_NAME.app
#   shell/assembly.sh start [--counters] writes the agents file and opens the bundle with `open`, so WebKit's
#                                       processes are attributed to it. --counters turns on the lag meter
#                                       (?counters=1); ASSEMBLY_QUERY sets any other query; ASSEMBLY_REMOTE=
#                                       workbox-test adds that machine (its host on BOX_PORT). The backend's log
#                                       is DATA/mac/backend.log
#   shell/assembly.sh stop              quits it as Cmd-Q does, so the shell stops the backend; waits for the pid
#                                       in DATA/mac/app.pid to exit
# ADAPTERS is where the ACP adapters are installed (default ~/spike-dotnet/adapters, the spike's, so nothing is
# downloaded). Builds share the machine: run at most 2 or 3 at once across lanes.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lane-env.sh
mode=${1:?usage: shell/assembly.sh build|start [--counters]|stop}
home=$DATA/mac
bundle="$home/$APP_NAME.app"
pidfile=$home/app.pid
running() { [ -f "$pidfile" ] && kill -0 "$(cat "$pidfile")" 2>/dev/null; }

case "$mode" in
  build)
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
    rm -rf shell/backend
    dotnet publish -v q --nologo -c Release -r osx-arm64 -p:PublishAot=true -o shell/backend dotnet/Assembly.Desktop
    rm -rf shell/backend/*.dbg shell/backend/*.dSYM
    cat > shell/backend/lane.json <<JSON
{ "port": $DESKTOP_PORT, "home": "$home", "tokenFile": "$TOKEN_FILE", "remotePort": $BOX_PORT, "remoteTokenFile": "$BOX_TOKEN_FILE" }
JSON
    config=$(printf '{"identifier":"%s","productName":"%s"}' "$APP_ID" "$APP_NAME")
    (cd shell && pnpm install --silent && pnpm tauri build --bundles app --config "$config" >/dev/null)
    mkdir -p "$home"
    rm -rf "$bundle"
    cp -R "shell/target/release/bundle/macos/$APP_NAME.app" "$bundle"
    echo "built $bundle from $(git rev-parse --short HEAD)" ;;
  start)
    if running; then echo "$APP_NAME is already running (pid $(cat "$pidfile"))" >&2; exit 1; fi
    [ -d "$bundle" ] || { echo "no bundle yet; run shell/assembly.sh build" >&2; exit 1; }
    mkdir -p "$home"
    dotnet/scripts/agents-file.sh "${ADAPTERS:-$HOME/spike-dotnet/adapters}" "$home/agents.json"
    query=${ASSEMBLY_QUERY:-}
    [ "${2:-}" = --counters ] && query=counters=1
    envs=(--env "PATH=$PATH")
    [ -n "${ASSEMBLY_REMOTE:-}" ] && envs+=(--env "ASSEMBLY_REMOTE=$ASSEMBLY_REMOTE")
    [ -n "$query" ] && envs+=(--env "ASSEMBLY_QUERY=$query")
    open -n "${envs[@]}" "$bundle"
    for _ in $(seq 100); do nc -z 127.0.0.1 "$DESKTOP_PORT" >/dev/null 2>&1 && break; sleep 0.2; done
    pid=$(pgrep -nf "$bundle/Contents/MacOS/assembly-shell" || true)
    [ -n "$pid" ] || { echo "$APP_NAME didn't start; see $home/backend.log" >&2; exit 1; }
    echo "$pid" > "$pidfile"
    echo "$APP_NAME (pid $pid) runs; its backend is on 127.0.0.1:$DESKTOP_PORT; log: $home/backend.log" ;;
  stop)
    running || { echo "$APP_NAME isn't running" >&2; exit 1; }
    pid=$(cat "$pidfile")
    # A plain SIGTERM would end the shell before it stops the backend.
    osascript -e "tell application id \"$APP_ID\" to quit"
    for _ in $(seq 150); do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
    if kill -0 "$pid" 2>/dev/null; then echo "$APP_NAME (pid $pid) is still running after 30 s" >&2; exit 1; fi
    rm -f "$pidfile"
    echo "$APP_NAME (pid $pid) stopped" ;;
  *) echo "unknown mode: $mode" >&2; exit 2 ;;
esac
