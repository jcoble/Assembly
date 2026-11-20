#!/usr/bin/env bash
# The verification bench: this lane's window on a private Xvfb display with accessibility on, its Workbox Test host,
# the machine saved, a local and a remote project, seeded data and both logins checked. Journeys run against it.
#   scripts/bench.sh up      builds what changed and starts it all; prints "bench up" and the state file
#   scripts/bench.sh down    stops everything up started, by saved pid, even after a failed or half-done up
# Run on the Workbox from a lane worktree (.lane). Exit codes: 0 up (blocked logins listed), 1 failed.
# Each resource is written to $DATA/bench/pids as it starts, so a failure or a signal unwinds only what started.
# Logins are checked but don't stop up: a case whose agent can't sign in is reported BLOCKED by the journey.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lane-env.sh
export PATH=$HOME/.local/bin:$HOME/.cargo/bin:$PATH MSBUILDDISABLENODEREUSE=1 DATA
bench=$DATA/bench
pids=$bench/pids
state=$bench/state.json

say() { printf 'bench: %s\n' "$*"; }
# Stop one pid we started and check it is gone; say which were already gone. Returns 1 if it won't stop.
stop_pid() {
  local name=$1 pid=$2
  if ! kill -0 "$pid" 2>/dev/null; then say "$name (pid $pid) was already gone"; return 0; fi
  kill "$pid" 2>/dev/null || true
  for _ in {1..50}; do kill -0 "$pid" 2>/dev/null || { say "$name (pid $pid) stopped"; return 0; }; sleep 0.2; done
  kill -KILL "$pid" 2>/dev/null || true
  sleep 0.5
  if kill -0 "$pid" 2>/dev/null; then say "$name (pid $pid) would not stop"; return 1; fi
  say "$name (pid $pid) killed after it ignored TERM"
}

down() {
  local failed=0 dbus='' atspi='' host='' app='' backend='' xvfb='' tunnel='' openbox=''
  [ -f "$pids" ] && . "$pids"
  [ -z "$openbox" ] || stop_pid 'window manager' "$openbox" || failed=1
  # The window: ui-check.sh's own stop when its state is there, else the pids we copied from it.
  if [ -f "$DATA/ui-check/state" ]; then scripts/ui-check.sh stop || failed=1
  else
    for name in app backend xvfb; do [ -z "${!name}" ] || stop_pid "window $name" "${!name}" || failed=1; done
  fi
  [ -z "$tunnel" ] || stop_pid 'tunnel to the host' "$tunnel" || failed=1
  [ -z "$atspi" ] || stop_pid 'AT-SPI bus launcher' "$atspi" || failed=1
  [ -z "$dbus" ] || stop_pid 'D-Bus session' "$dbus" || failed=1
  # Stopping a unit that isn't running is harmless, so the host is always attempted.
  if said=$(dotnet/scripts/workbox.sh stop 2>&1); then say "Workbox Test host $BOX_UNIT stopped"
  elif grep -q 'not loaded' <<< "$said"; then say "Workbox Test host $BOX_UNIT was not installed"
  else say "Workbox Test host $BOX_UNIT would not stop: $(tail -1 <<< "$said")"; failed=1; fi
  if [ "$failed" = 0 ]; then rm -f "$pids" "$state"; say 'bench down'
  else say "some of it would not stop; kept $pids"; return 1; fi
}

up() {
  [ ! -f "$pids" ] || { echo 'bench is up; run scripts/bench.sh down first' >&2; exit 1; }
  ulimit -n 65536
  [ "$(free -g | awk '/^Mem:/ {print $7}')" -gt 6 ] || { echo '6 GB or less available; not starting' >&2; exit 1; }
  heavy=$(pgrep -c -f 'cargo build|cargo test|rustc|dotnet (build|publish|test)|MSBuild.dll' || true)
  [ "$heavy" -lt 3 ] || { echo "$heavy heavy jobs running; at most three on the machine" >&2; exit 1; }
  mkdir -p "$bench"
  : > "$pids"
  trap 'say "up failed at: $BASH_COMMAND"; down || true; exit 1' ERR
  trap 'say "interrupted"; down || true; exit 130' INT TERM

  say 'build'
  node scripts/drive/build.ts
  say 'seed'
  profile=$(node scripts/seed-dev.ts --worktree "$root" --if-empty | sed -n 's/^Dev profile: //p')
  [ -n "$profile" ]
  export ASSEMBLY_UI_CHECK_PROFILE=$profile
  say 'adapters (built locally, the way app:dev does)'
  versions=$(node scripts/package-adapters.ts resolve)
  # One bundle per set of versions, built once: the packager refuses to overwrite a bundle.
  adapters=$DATA/local-adapters/$(printf '%s' "$versions" | sha256sum | cut -c1-16)
  if [ ! -f "$adapters/assembly-adapters-linux-x64.tar.gz" ]; then
    rm -rf "$adapters"
    node scripts/package-adapters.ts build linux-x64 "$versions" "$adapters" > "$bench/adapters.log" 2>&1
  fi
  export ASSEMBLY_LOCAL_ADAPTERS=$adapters

  say 'accessibility: private D-Bus session and AT-SPI'
  read -r address dbus < <(dbus-daemon --session --fork --print-address=1 --print-pid=1 | paste -sd' ')
  printf 'dbus=%s\n' "$dbus" >> "$pids"
  export DBUS_SESSION_BUS_ADDRESS=$address
  setsid /usr/libexec/at-spi-bus-launcher --launch-immediately > "$bench/atspi.log" 2>&1 &
  printf 'atspi=%s\n' "$!" >> "$pids"
  for _ in {1..50}; do busctl --user status org.a11y.Bus >/dev/null 2>&1 && break; sleep 0.1; done
  busctl --user set-property org.a11y.Bus /org/a11y/bus org.a11y.Status IsEnabled b true
  [ "$(busctl --user get-property org.a11y.Bus /org/a11y/bus org.a11y.Status IsEnabled)" = 'b true' ]

  say "Workbox Test host $BOX_UNIT"
  printf 'host=%s\n' "$BOX_UNIT" >> "$pids"
  dotnet/scripts/workbox.sh serve > "$bench/host.log" 2>&1 || { tail -20 "$bench/host.log" >&2; false; }

  say 'window'
  scripts/ui-check.sh start > "$bench/window.log"
  grep -E '^(xvfb|app|backend)=' "$DATA/ui-check/state" >> "$pids"
  display=$(sed -n 's/^DISPLAY=//p' "$DATA/ui-check/state")
  # agent-desktop lists windows through the window manager (_NET_CLIENT_LIST), so the display gets one: openbox, with
  # the window undecorated and at 0,0, where the tree's bounds and the screen agree.
  say 'window manager'
  cat > "$bench/openbox.xml" <<'XML'
<?xml version="1.0"?>
<openbox_config xmlns="http://openbox.org/3.4/rc">
  <theme><keepBorder>no</keepBorder></theme>
  <applications>
    <application type="normal"><decor>no</decor><position force="yes"><x>0</x><y>0</y></position></application>
  </applications>
</openbox_config>
XML
  DISPLAY=$display setsid openbox --sm-disable --config-file "$bench/openbox.xml" > "$bench/openbox.log" 2>&1 < /dev/null &
  printf 'openbox=%s\n' "$!" >> "$pids"
  for _ in {1..50}; do DISPLAY=$display xwininfo -id "$(head -1 "$DATA/ui-check/window")" | grep -q 'Absolute upper-left Y:  0' && break; sleep 0.1; done
  DISPLAY=$display xwininfo -id "$(head -1 "$DATA/ui-check/window")" | grep -q 'Absolute upper-left Y:  0'

  say 'machine, projects and logins'
  BENCH_STATE=$state BENCH_PIDS=$pids BENCH_DISPLAY=$display BENCH_DBUS=$DBUS_SESSION_BUS_ADDRESS BENCH_APP_LOG=$DATA/ui-check/app.log \
    DESKTOP_PORT=$DESKTOP_PORT TOKEN_FILE=$TOKEN_FILE BOX_PORT=$BOX_PORT BOX_TOKEN_FILE=$BOX_TOKEN_FILE BOX_DATA=$BOX_DATA \
    DATA=$DATA node scripts/drive/bench-setup.ts
  trap - ERR INT TERM
  say "bench up; state $state"
  printf 'export BENCH_STATE=%q DISPLAY=%q DBUS_SESSION_BUS_ADDRESS=%q\n' "$state" "$display" "$DBUS_SESSION_BUS_ADDRESS" > "$bench/env"
}

case "${1:-}" in
  up) up ;;
  down) down ;;
  *) echo 'usage: scripts/bench.sh up|down' >&2; exit 2 ;;
esac
