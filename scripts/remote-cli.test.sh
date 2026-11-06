#!/usr/bin/env bash
# Argument validation on an already-built CLI; never starts a provider or host.
# Run: bash scripts/remote-cli.test.sh [path/to/Assembly.Remote.dll]
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
backend=${1:-$root/dotnet/Assembly.Remote/bin/Debug/net10.0/Assembly.Remote.dll}
[[ -f "$backend" ]] || { echo "Build Assembly.Remote first: $backend" >&2; exit 1; }

record='usage: Assembly.Remote record --out DIR [--only name,name] -- <adapter command...>'
checks=0
expect_usage() {
  local expected=$1 output code=0
  shift
  output=$(dotnet "$backend" "$@" 2>&1) || code=$?
  if [[ "$code" != 2 || "$output" != "$expected" ]]; then
    printf 'FAIL arguments: %s (exit %s)\n%s\n' "$*" "$code" "$output" >&2
    exit 1
  fi
  checks=$((checks + 1))
}

# Invalid serve arguments cannot launch a host. Reuse its full usage for top-level validation.
code=0
serve=$(dotnet "$backend" serve --usage 2>&1) || code=$?
[[ "$code" == 2 && "$serve" == 'usage: Assembly.Remote serve '* ]] || { echo "FAIL serve usage" >&2; exit 1; }
for option in --port --home --token-file --agents; do
  [[ "$serve" == *"$option "* ]] || { echo "FAIL missing serve option: $option" >&2; exit 1; }
done
for command in '' --usage unknown smoke rows; do
  if [[ -z "$command" ]]; then
    expect_usage "$record"$'\n'"$serve"
  else
    expect_usage "$record"$'\n'"$serve" "$command"
  fi
done
expect_usage "$record" record
expect_usage "$record" record --usage
expect_usage "$record" record --out
printf 'PASS %s CLI usage checks plus serve validation (no provider or host started)\n' "$checks"
