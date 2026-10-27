#!/usr/bin/env bash
# The window's guard (desktop/scripts/guard.sh): no timers outside its clock, no sockets or HTTP outside its socket
# client, no sleeping, and no file reads in code that draws.
cd "$(dirname "$0")/../.."
bash desktop/scripts/guard.sh
