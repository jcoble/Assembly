# Sourced by the scripts that build, run or deploy Assembly: this checkout's values. Main has no .lane file and
# keeps the defaults; a lane worktree (scripts/lane.sh new) has one with LANE_NAME and LANE_NUMBER (1 to 9), and
# gets its own ports, folders, app id and box unit from them, so several lanes run side by side.
root=$(git rev-parse --show-toplevel)
LANE_NAME="" LANE_NUMBER=0
[ -f "$root/.lane" ] && . "$root/.lane"
suffix=${LANE_NAME:+-$LANE_NAME}
MAC_PORT=$((18797 + LANE_NUMBER * 10))   # the Mac's backend
DEV_PORT=$((1430 + LANE_NUMBER))         # vite dev
BOX_PORT=$((9797 + LANE_NUMBER * 10))    # the box host; the Mac reaches it on BOX_PORT + 10000
DATA=$HOME/assembly-dotnet$suffix        # the Mac's data: mac/ (database, threads, logs, the bundle)
TOKEN_FILE=$HOME/.config/assembly-dotnet$suffix/env
BOX_DATA=assembly-dotnet$suffix          # on the box, under its home: serve/, bin/
BOX_TOKEN_FILE=.config/assembly-dotnet$suffix/env
BOX_UNIT=assembly-dotnet$suffix
APP_ID=dev.jcoble.assembly${LANE_NAME:+.$LANE_NAME}
APP_NAME="Assembly${LANE_NAME:+ $LANE_NAME}"
