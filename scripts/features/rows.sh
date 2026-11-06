#!/usr/bin/env bash
# Wait for the actual turn-end row, then check snapshot order, prevSeq and resume through retained sockets.
set -euo pipefail
node "$(dirname "$0")/live-rows.ts" --check
