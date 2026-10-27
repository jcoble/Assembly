#!/usr/bin/env bash
# The window's guard (desktop/scripts/guard.sh): no sockets or HTTP outside its socket client, no detached tasks, no
# threads outside the socket client and the backend launcher, and no file reads in code that draws.
cd "$(dirname "$0")/../.."
bash desktop/scripts/guard.sh
