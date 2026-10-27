#!/usr/bin/env bash
# The backend has to finish stopping inside the shell's wait after SIGTERM, or the shell kills it with saves still
# queued. Reads both numbers from source and fails unless the backend's ShutdownTimeout is below the shell's wait.
# Run: bash scripts/shutdown-timing.test.sh
set -uo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
backend=$(grep -oE 'ShutdownTimeout = TimeSpan\.FromSeconds\(([0-9]+)\)' "$root/dotnet/Assembly.Infrastructure/Http/ApiServer.cs" | grep -oE '[0-9]+')
shell=$(grep -oE 'BACKEND_STOP_WAIT: Duration = Duration::from_secs\(([0-9]+)\)' "$root/shell/src/main.rs" | grep -oE '[0-9]+')
if [ -z "$backend" ] || [ -z "$shell" ]; then
  echo "FAIL backend ShutdownTimeout (${backend:-not found}) or shell BACKEND_STOP_WAIT (${shell:-not found}) not found"
  exit 1
fi
if [ "$backend" -lt "$shell" ]; then echo "ok   backend stops within ${backend} s, inside the shell's ${shell} s"
else echo "FAIL backend ShutdownTimeout ${backend} s isn't below the shell's ${shell} s wait"; exit 1; fi
