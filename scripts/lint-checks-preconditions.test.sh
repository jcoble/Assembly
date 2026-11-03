#!/usr/bin/env bash
# Run the actual fixture script against invalid source states; no .NET/lint suite is started.
set -euo pipefail
check=$(cd "$(dirname "$0")" && pwd)/lint-checks.test.sh
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT

start() {
  repo=$scratch/$1
  mkdir -p "$repo/scripts" "$repo/dotnet"
  cp "$check" "$repo/scripts/lint-checks.test.sh"
  git -C "$repo" init -qb main
  git -C "$repo" config user.name 'Lint fixture'
  git -C "$repo" config user.email lint@example.invalid
  git -C "$repo" config core.hooksPath /dev/null
  git -C "$repo" config commit.gpgsign false
  printf 'base\n' >"$repo/dotnet/source.cs"
  git -C "$repo" add .
  git -C "$repo" commit -qm base
}
reject() {
  local reason=$1 result=0
  bash "$repo/scripts/lint-checks.test.sh" >"$scratch/output" 2>&1 || result=$?
  if [ "$result" != 1 ] || ! grep -qF "lint fixtures: $reason" "$scratch/output"; then
    cat "$scratch/output"
    echo "FAIL expected early rejection: $reason (exit $result)" >&2
    exit 1
  fi
  echo "ok   rejects $reason before fixtures"
}

start unstaged
printf 'changed\n' >>"$repo/dotnet/source.cs"
reject 'unstaged source'

start untracked
printf 'new\n' >"$repo/dotnet/new.cs"
reject 'untracked source'

start conflicted
blob=$(git -C "$repo" rev-parse HEAD:dotnet/source.cs)
printf '0 %040d\tdotnet/source.cs\n100644 %s 1\tdotnet/source.cs\n100644 %s 2\tdotnet/source.cs\n100644 %s 3\tdotnet/source.cs\n' \
  0 "$blob" "$blob" "$blob" | git -C "$repo" update-index --index-info
reject 'conflicted index'
