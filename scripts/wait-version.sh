#!/usr/bin/env bash
# Compatibility CLI: authenticated socket welcome, exact/same retained code commit; token on stdin.
# scripts/wait-version.sh <name> <base url> <commit> [seconds, default 30]
set -euo pipefail
cd "$(dirname "$0")/.."
exec node scripts/wait-welcome.ts "$@"
