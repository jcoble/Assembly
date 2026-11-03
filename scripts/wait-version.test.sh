#!/usr/bin/env bash
# Exercise the compatibility CLI and readiness contract against real WebSocket peers.
set -euo pipefail
exec node --test --test-timeout=10000 "$(dirname "$0")/wait-welcome.test.ts"
