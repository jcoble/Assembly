#!/usr/bin/env bash
# Lint, format, size and unused-code checks, before every commit (.githooks/pre-commit) and on every push to main:
# runs every scripts/lint.d/*.sh and fails if any of them fails. It's locked (scripts/locked-paths.txt).
cd "$(dirname "$0")/.."
failed=0
for check in scripts/lint.d/*.sh; do
  bash "$check" || failed=1
done
exit "$failed"
