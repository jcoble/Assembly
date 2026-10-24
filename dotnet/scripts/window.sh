#!/usr/bin/env bash
# Runs the Photino window (Assembly.Mac window): the backend on 127.0.0.1:18797 and the window, in one process, from
# a minimal app bundle, so it has a bundle id, a Dock icon and keyboard focus.
#   dotnet/scripts/window.sh build   publishes Assembly.Mac (Native AOT) and the web build, and lays out
#                                    ~/assembly-dotnet/mac/Assembly.app around them
#   dotnet/scripts/window.sh start   opens the bundle with `open`, so WebKit's processes are attributed to it;
#                                    ASSEMBLY_SOAK, when set, becomes the page's query; ASSEMBLY_DEVTOOLS=1 turns on
#                                    WebKit's context menu and inspector; ASSEMBLY_REMOTE=workbox-test adds that
#                                    machine (its Assembly.Remote serve, over an ssh tunnel). Port 18797 must be free
#                                    (dotnet/scripts/mac.sh stop). Its log is ~/assembly-dotnet/mac/window.log
#   dotnet/scripts/window.sh stop    quits it, by the pid in ~/assembly-dotnet/mac/window.pid
# Run mac.sh serve once first: it makes the test repo. start writes the agents file (agents-file.sh).
set -euo pipefail
cd "$(dirname "$0")/../.."
mode=${1:?usage: dotnet/scripts/window.sh build|start|stop}
home=~/assembly-dotnet/mac
bundle="$home/Assembly.app"
pidfile=$home/window.pid
running() { [ -f "$pidfile" ] && kill -0 "$(cat "$pidfile")" 2>/dev/null; }

case "$mode" in
  build)
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
    (cd app && pnpm build >/dev/null)
    rm -rf "$bundle"
    mkdir -p "$bundle/Contents/MacOS"
    dotnet publish -v q --nologo -c Release -r osx-arm64 -p:PublishAot=true -o "$bundle/Contents/MacOS" dotnet/Assembly.Mac
    cat > "$bundle/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>dev.jcoble.assembly</string>
  <key>CFBundleName</key><string>Assembly</string>
  <key>CFBundleExecutable</key><string>Assembly.Mac</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF
    echo "built $bundle from $(git rev-parse --short HEAD)" ;;
  start)
    if running; then echo "the window is already running (pid $(cat "$pidfile"))" >&2; exit 1; fi
    if nc -z 127.0.0.1 18797 >/dev/null 2>&1; then echo "18797 is in use; stop the backend first (dotnet/scripts/mac.sh stop)" >&2; exit 1; fi
    [ -d "$bundle" ] || { echo "no bundle yet; run dotnet/scripts/window.sh build" >&2; exit 1; }
    dotnet/scripts/agents-file.sh ~/assembly-dotnet/adapters "$home/agents.json"
    envs=(--env "PATH=$PATH")
    [ -n "${ASSEMBLY_SOAK:-}" ] && envs+=(--env "ASSEMBLY_SOAK=$ASSEMBLY_SOAK")
    [ -n "${ASSEMBLY_DEVTOOLS:-}" ] && envs+=(--env "ASSEMBLY_DEVTOOLS=$ASSEMBLY_DEVTOOLS")
    open -n "${envs[@]}" --stdout "$home/window.log" --stderr "$home/window.log" "$bundle" \
      --args window --app "$PWD/app/build" --port 18797 --home "$home" ${ASSEMBLY_REMOTE:+--remote "$ASSEMBLY_REMOTE"}
    for _ in $(seq 50); do nc -z 127.0.0.1 18797 >/dev/null 2>&1 && break; sleep 0.2; done
    pid=$(pgrep -nf "$bundle/Contents/MacOS/Assembly.Mac" || true)
    [ -n "$pid" ] || { echo "the window didn't start; see $home/window.log" >&2; tail -5 "$home/window.log" >&2; exit 1; }
    echo "$pid" > "$pidfile"
    echo "the window (pid $pid) runs on 127.0.0.1:18797; log: $home/window.log" ;;
  stop)
    running || { echo "the window isn't running" >&2; exit 1; }
    pid=$(cat "$pidfile")
    kill "$pid"
    for _ in $(seq 150); do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
    if kill -0 "$pid" 2>/dev/null; then echo "the window (pid $pid) is still running after 30 s" >&2; exit 1; fi
    rm -f "$pidfile"
    echo "the window (pid $pid) stopped" ;;
  *) echo "unknown mode: $mode" >&2; exit 2 ;;
esac
