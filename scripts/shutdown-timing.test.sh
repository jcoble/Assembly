#!/usr/bin/env bash
# Exercise the native launcher's real child lifecycle: SIGTERM, reaping, drop/join and grace-period escalation.
# Run: bash scripts/shutdown-timing.test.sh
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root/desktop"
output=$(mktemp)
trap 'rm -f "$output"' EXIT

if cargo test --locked core::backend::launcher::tests -- --color never | tee "$output"; then
  if ! grep -Eq '^test result: ok\. [1-9][0-9]* passed; 0 failed;' "$output"; then
    echo "FAIL native launcher lifecycle tests did not run" >&2
    exit 1
  fi
else
  exit "$?"
fi
