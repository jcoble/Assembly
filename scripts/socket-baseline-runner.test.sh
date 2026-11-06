#!/usr/bin/env bash
# Exercise the actual CI wrapper with disposable suites; no source-text matching or heavy build.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
mkdir -p "$scratch/scripts"
cp "$here/socket-baseline.test.sh" "$scratch/scripts/"
suites=(check4-case socket-rows terminal-socket workbox-row-log)

prepare() {
  local suite
  : > "$scratch/ran"
  for suite in "${suites[@]}"; do
    cat > "$scratch/scripts/$suite.test.ts" <<EOF_SUITE
import { appendFileSync } from 'node:fs';
import { test } from 'node:test';
test('$suite actually runs', () => { appendFileSync(process.env.RUNNER_PROOF!, '$suite\\n'); });
EOF_SUITE
  done
}
run() {
  result=0
  RUNNER_PROOF="$scratch/ran" bash "$scratch/scripts/socket-baseline.test.sh" > "$scratch/output" 2>&1 || result=$?
}
prepare
run
[ "$result" = 0 ] || { cat "$scratch/output"; exit 1; }
for suite in "${suites[@]}"; do
  [ "$(grep -cx "$suite" "$scratch/ran")" = 1 ] || { echo "FAIL $suite did not execute exactly once"; exit 1; }
done
[ "$(wc -l < "$scratch/ran")" = 4 ] || { echo 'FAIL unexpected suite invocation count'; exit 1; }
echo 'ok the real wrapper executes all four suites exactly once'

for suite in "${suites[@]}"; do
  prepare
  rm "$scratch/scripts/$suite.test.ts"
  run
  [ "$result" != 0 ] || { echo "FAIL missing $suite passed"; exit 1; }
  echo "ok missing $suite fails the wrapper"

  prepare
  cat >> "$scratch/scripts/$suite.test.ts" <<'EOF_FAILURE'
test('real test failure', () => { throw new Error('intentional runner failure'); });
EOF_FAILURE
  run
  [ "$result" != 0 ] || { echo "FAIL failing $suite passed"; exit 1; }
  for sibling in "${suites[@]}"; do
    [ "$(grep -cx "$sibling" "$scratch/ran")" = 1 ] || { echo "FAIL $sibling was not exercised after a sibling failed"; exit 1; }
  done
  echo "ok failing $suite returns nonzero while every suite still executes"
done
