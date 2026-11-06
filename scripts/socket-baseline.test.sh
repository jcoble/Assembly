#!/usr/bin/env bash
# Retained Node24 socket suites enter CI through its existing scripts/*.test.sh loop.
# wait-welcome.test.ts is already run by wait-version.test.sh.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
suites=(
  "$here/check4-case.test.ts"
  "$here/socket-rows.test.ts"
  "$here/terminal-socket.test.ts"
  "$here/workbox-row-log.test.ts"
)
for suite in "${suites[@]}"; do
  [ -f "$suite" ] || { echo "missing required socket suite: $suite" >&2; exit 1; }
done
exec node --test --test-timeout=30000 "${suites[@]}"
