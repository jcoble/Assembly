#!/usr/bin/env bash
# Exercise the terminal on the case's owning backend, and remote forwarding through the desktop.
set -euo pipefail
exec node "$(dirname "$0")/terminal.ts"
