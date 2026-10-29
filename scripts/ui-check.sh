#!/usr/bin/env bash
# Native GPUI window on a private Xvfb display. Run from this lane's checkout.
# Build first, only when sources change (one build at a time, after free -g):
# . scripts/lane-env.sh; export CARGO_TARGET_DIR=$DATA/target
# nice -n 10 cargo build --locked --manifest-path desktop/Cargo.toml
# export NUGET_HTTP_CACHE_PATH=$DATA/ui-check/nuget-http-cache MSBUILDDISABLENODEREUSE=1
# nice -n 10 dotnet publish -c Release -o "$DATA/ui-check/backend" dotnet/Assembly.Desktop
# Deploy this lane's Workbox Test host separately, with those exports still set:
# dotnet/scripts/workbox.sh serve lane/ui-check
# Remote: Settings > Connections where the branch has it; before that, start with ASSEMBLY_REMOTE=workbox-test.
# Connect only to Workbox Test.
# With the printed DISPLAY exported, type: xdotool type --clearmodifiers 'text'
# Scroll: xdotool click 5 (down), xdotool click 4 (up).
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lane-env.sh
home=$DATA/ui-check
state=$home/state
export NUGET_HTTP_CACHE_PATH=$home/nuget-http-cache MSBUILDDISABLENODEREUSE=1

stop() {
  # The app owns its backend; closing its stdin also stops it after a force quit.
  for pid in "${app:-}" "${backend:-}" "${xvfb:-}"; do
    [ -n "$pid" ] || continue
    if kill -0 "$pid" 2>/dev/null; then
      kill "$pid"
      for _ in {1..100}; do
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.2
      done
      if kill -0 "$pid" 2>/dev/null; then
        echo "pid $pid is still present; state kept at $state" >&2
        return 1
      fi
    fi
    echo "pid $pid is gone"
  done
  rm -f "$state"
}
case "${1:-}" in
  start)
    trap 'echo "ui-check: start failed at: $BASH_COMMAND (logs: $home/xvfb.log, $home/app.log)" >&2' ERR
    [ ! -f "$state" ] || { echo 'stop this lane first' >&2; exit 1; }
    [ "$(free -g | awk '/^Mem:/ {print $7}')" -gt 6 ] || { echo '6 GB or less available; not starting' >&2; exit 1; }
    [ -x "$DATA/target/debug/assembly-desktop" ]
    [ -x "$home/backend/Assembly.Desktop" ]
    mkdir -p "$home/runtime" "$home/cache" "$home/data" "$home/mac"
    chmod 700 "$home/runtime"
    # All mutable paths, including the backend token, stay inside this lane's DATA.
    cat > "$home/backend/lane.json" <<JSON
{"port":$DESKTOP_PORT,"home":"$home/mac","tokenFile":"$home/token.env","remotePort":$BOX_PORT,"remoteTokenFile":"$BOX_TOKEN_FILE"}
JSON
    export XDG_RUNTIME_DIR=$home/runtime XDG_CACHE_HOME=$home/cache XDG_DATA_HOME=$home/data
    export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json
    export ASSEMBLY_BACKEND_DIR=$home/backend
    unset WAYLAND_DISPLAY
    app= backend= xvfb=
    trap 'stop' EXIT
    : > "$home/display"
    setsid Xvfb -displayfd 3 -screen 0 1710x990x24 -nolisten tcp 3> "$home/display" > "$home/xvfb.log" 2>&1 &
    xvfb=$!
    printf 'xvfb=%s\n' "$xvfb" > "$state"
    for _ in {1..50}; do
      [ -s "$home/display" ] && break
      kill -0 "$xvfb"
      sleep 0.1
    done
    [ -s "$home/display" ]
    export DISPLAY=:$(cat "$home/display")
    printf 'DISPLAY=%s\n' "$DISPLAY" >> "$state"
    setsid nohup nice -n 10 "$DATA/target/debug/assembly-desktop" > "$home/app.log" 2>&1 &
    app=$!
    printf 'app=%s\n' "$app" >> "$state"
    # Wait for this app's backend and native window, rather than assume a delay.
    for _ in {1..100}; do
      backend=$(ps -o pid= --ppid "$app" | awk 'NR==1 {print $1}' || true)
      [ -z "$backend" ] || break
      kill -0 "$app"
      sleep 0.1
    done
    [ -n "$backend" ]
    printf 'backend=%s\n' "$backend" >> "$state"
    timeout 60 xdotool search --sync --onlyvisible --name '^Assembly$' > "$home/window"
    kill -0 "$backend"
    # Screenshots are window pixels and xdotool clicks are screen pixels, so pin the window at 0,0.
    xdotool windowsize "$(head -1 "$home/window")" 1710 990 windowmove "$(head -1 "$home/window")" 0 0
    trap - EXIT
    echo "DISPLAY=$DISPLAY"
    ;;
  stop) [ -f "$state" ]; . "$state"; stop ;;
  shot) . "$state"; export DISPLAY; "$HOME/.local/bin/assembly-native-ui" shot "${2:?shot PATH}" ;;
  *) echo 'usage: scripts/ui-check.sh start|stop|shot PATH' >&2; exit 2 ;;
esac
