#!/usr/bin/env bash
# Exercise the terminal on the case's owning backend, before remote terminal forwarding exists.
set -euo pipefail
exec node "$(dirname "$0")/terminal.ts"
