#!/usr/bin/env bash
# Exercise the real gate against throwaway Git histories, never the owner's repository/evidence.
set -euo pipefail
check=$(cd "$(dirname "$0")" && pwd)/check-evidence.sh
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
failed=0
fixture=0

start() {
  fixture=$((fixture + 1))
  repo=$scratch/$fixture
  mkdir -p "$repo/scripts/features"
  cp "$check" "$repo/scripts/check-evidence.sh"
  cd "$repo"
  git init -q -b main
  git config user.name 'Evidence fixture'
  git config user.email evidence@example.invalid
  git config core.hooksPath /dev/null
  git config commit.gpgsign false
  printf '# scenario\n' >scripts/features/rows.sh
  printf '# scenario\n' >scripts/features/tool-detail.sh
  git add .
  git commit -qm base
  base=$(git rev-parse HEAD)
}
change() {
  mkdir -p "$(dirname "$1")"
  printf 'fixture %s\n' "$fixture" >"$1"
  git add "$1"
  git commit -qm "change $1"
  code=$(git rev-parse HEAD)
}
summary() {
  local target=$1 pass=${2:-true} partial=${3:-false} features=${4:-'["rows","tool-detail"]'}
  mkdir -p "evidence/$target/four-way"
  jq -n --arg commit "$target" --argjson pass "$pass" --argjson partial "$partial" --argjson features "$features" \
    '{commit:$commit,pass:$pass,partial:$partial,features:$features}' >"evidence/$target/four-way/summary.json"
  git add evidence
  git commit -qm evidence
}
expect() {
  local want=$1 name=$2 actual=0
  bash scripts/check-evidence.sh "$base" HEAD >"$scratch/output" 2>&1 || actual=$?
  if [ "$actual" = "$want" ]; then printf 'ok   %s\n' "$name"
  else printf 'FAIL %s: wanted %s, got %s\n' "$name" "$want" "$actual"; cat "$scratch/output"; failed=1; fi
}

for path in desktop/src/main.rs desktop/Cargo.toml desktop/Cargo.lock desktop/rust-toolchain.toml desktop/themes/assembly.json \
  dotnet/Assembly.Desktop/Program.cs app/src/old.ts app/package.json app/pnpm-lock.yaml shell/src/main.rs shell/Cargo.toml shell/Cargo.lock; do
  start
  change "$path"
  expect 1 "$path requires evidence"
  summary "$code"
  expect 0 "$path accepts a complete same-commit check"
done

start
change desktop/src/main.rs
summary "$code" true true
expect 1 'a partial check does not cover code'

start
change desktop/src/main.rs
summary "$code" true false '["rows"]'
expect 1 'a missing feature does not cover code'

start
change desktop/src/main.rs
summary "$code" true false '["rows","detail"]'
expect 1 'a substring does not cover the tool-detail scenario'

start
change desktop/src/main.rs
summary "$code" false
expect 1 'a failed check does not cover code'

start
change desktop/src/main.rs
change desktop/src/later.rs
summary "$code"
expect 0 'a complete later descendant check covers earlier code'

start
change desktop/src/main.rs
change scripts/features/new.sh
summary "$code"
expect 1 'later evidence must run features declared at its own commit'
summary "$code" true false '["rows","tool-detail","new"]'
expect 0 'later evidence including its new feature covers earlier code'

start
change desktop/src/main.rs
main_code=$code
git checkout -qb sibling "$base"
change desktop/src/other.rs
sibling_code=$code
git checkout -q main
summary "$sibling_code"
expect 1 'a sibling check does not cover a nonancestor code commit'
summary "$main_code"
expect 0 'same-commit evidence covers code despite an unrelated summary'

start
change dotnet/Assembly.Unknown/Program.cs
summary "$code"
expect 1 'an unclassified Assembly project fails even with evidence'

start
change dotnet/Assembly.Tests/Example.cs
expect 0 'a classified test-only change does not require runtime evidence'

exit "$failed"
