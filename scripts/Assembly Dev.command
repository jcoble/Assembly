#!/bin/bash
set -euo pipefail
# Finder opens .command files with a small PATH. The TypeScript launcher owns all build/data/remote decisions.
export PATH="$HOME/.cargo/bin:$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/local/share/dotnet:/usr/bin:/bin:$PATH"
root=$(cd "$(dirname "$0")/.." && pwd)
exec node "$root/scripts/app-dev.ts" "$@"
