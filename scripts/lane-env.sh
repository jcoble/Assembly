# Sourced by the scripts that build, run or deploy Assembly: this checkout's values. Main has no .lane file and
# keeps the defaults; a lane worktree (scripts/lane.sh new) has one with LANE_NAME and LANE_NUMBER (1 to 9), and
# gets its own ports, folders, app id and box unit from them, so several lanes run side by side.
root=$(git rev-parse --show-toplevel)
LANE_NAME="" LANE_NUMBER=0
[ -f "$root/.lane" ] && . "$root/.lane"
# With no .lane a checkout gets main's ports, folders and Workbox Test host, so only main may have none.
if [ ! -f "$root/.lane" ] && [ "$(git -C "$root" branch --show-current)" != main ]; then
  echo "stop: $root has no .lane and isn't on main, so it would use main's ports, data and Workbox Test host; copy its lane's .lane in (scripts/lane.sh new writes one)" >&2
  return 1
fi
# A .lane with no name, a name that isn't scripts/lane.sh's kind (a slash could reach main's folders), or a number
# outside 1 to 9 would land on main's values or past the lane ports.
if [ -f "$root/.lane" ] && ! [[ $LANE_NAME =~ ^[a-z0-9][a-z0-9-]*$ && $LANE_NUMBER =~ ^[1-9]$ ]]; then
  echo "stop: $root/.lane needs a LANE_NAME of lowercase letters, digits and dashes and a LANE_NUMBER from 1 to 9" >&2
  return 1
fi
suffix=${LANE_NAME:+-$LANE_NAME}
MAC_PORT=$((18797 + LANE_NUMBER * 10))   # the Mac's backend
DEV_PORT=$((1430 + LANE_NUMBER))         # vite dev
BOX_PORT=$((9797 + LANE_NUMBER * 10))    # the box host; the Mac reaches it on BOX_PORT + 10000
DATA=$HOME/assembly-dotnet$suffix        # the Mac's data: mac/ (database, sessions, logs, the bundle)
TOKEN_FILE=$HOME/.config/assembly-dotnet$suffix/env
BOX_DATA=assembly-dotnet$suffix          # on the box, under its home: serve/, bin/
BOX_TOKEN_FILE=.config/assembly-dotnet$suffix/env
BOX_UNIT=assembly-dotnet$suffix
APP_ID=dev.jcoble.assembly${LANE_NAME:+.$LANE_NAME}
APP_NAME="Assembly${LANE_NAME:+ $LANE_NAME}"
