#!/usr/bin/env bash
# Tests for the checks in scripts/lint.d/: each one runs in a throwaway repo, fails on a bad sample and passes once
# the sample is fixed. Run: bash scripts/lint-checks.test.sh
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
failed=0

# A throwaway repo holding one lint.d script; prints its path.
repo_with() {
  local repo
  repo=$(mktemp -d)
  git -C "$repo" init -q
  mkdir -p "$repo/scripts/lint.d"
  cp "$here/lint.d/$1" "$repo/scripts/lint.d/"
  echo "$repo"
}
# expect <exit code> <name> <repo> <script>
expect() {
  git -C "$3" add -A
  "$3/scripts/lint.d/$4" >/dev/null 2>&1
  local got=$?
  if [ "$got" = "$1" ]; then echo "ok   $2"; else echo "FAIL $2: exit $got, wanted $1"; failed=1; fi
}

r=$(repo_with 10-no-engine.sh)
mkdir -p "$r/dotnet/Assembly.Domain" && echo x >"$r/dotnet/Assembly.Domain/A.cs"
expect 0 "no-engine: a tree without Assembly.Engine passes" "$r" 10-no-engine.sh
mkdir -p "$r/dotnet/Assembly.Engine" && echo x >"$r/dotnet/Assembly.Engine/A.cs"
expect 1 "no-engine: dotnet/Assembly.Engine/ coming back fails" "$r" 10-no-engine.sh
rm -rf "$r"

r=$(repo_with 30-provider-branches.sh)
mkdir -p "$r/app/src/lib" "$r/dotnet/Assembly.Application/Common/Acp/V1" "$r/dotnet/Assembly.Tests" "$r/shell/src"
echo "const agent = pick(); // a comment that says claude is fine" >"$r/app/src/lib/a.ts"
echo 'if (name == "claude") { }' >"$r/dotnet/Assembly.Application/Common/Acp/V1/Reader.cs"
echo 'Assert.True(kind == "codex");' >"$r/dotnet/Assembly.Tests/T.cs"
expect 0 "provider branches: none outside Acp/V1 and the tests passes" "$r" 30-provider-branches.sh
for bad in "if (agent === 'claude') go();" "case 'codex': break;" "x = agent == \"antigravity\" ? a : b" \
  "Agent::Claude => run()," "if matches!(a, Agent::Codex) {}" "const label = 'codex' !== agent"; do
  echo "$bad" >"$r/app/src/lib/a.ts"
  expect 1 "provider branches: fails on: $bad" "$r" 30-provider-branches.sh
done
rm -rf "$r"

exit "$failed"
