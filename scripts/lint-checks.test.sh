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
# The same, holding dotnet/ as committed and an empty app/src (the architecture tests find the root by dotnet/ and app/).
dotnet_repo_with() {
  local repo
  repo=$(repo_with "$1")
  git -C "$here/.." archive HEAD dotnet | tar -x -C "$repo"
  mkdir -p "$repo/app/src"
  echo "$repo"
}
# Commits the repo's tree, so what's staged next is all a commit would change.
commit_all() {
  git -C "$1" add -A
  git -C "$1" -c user.name=lint -c user.email=lint@test.invalid commit -qm tree
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

r=$(repo_with 20-file-size.sh)
mkdir -p "$r/dotnet/Assembly.Domain" "$r/app/src/lib" "$r/app/node_modules/x" "$r/shell/src"
seq 600 >"$r/dotnet/Assembly.Domain/A.cs"
printf '%s\n' $(seq 599) >"$r/app/src/lib/B.svelte" && printf 'last' >>"$r/app/src/lib/B.svelte"
seq 900 >"$r/app/node_modules/x/big.ts"
seq 900 >"$r/app/src/lib/notes.md"
expect 0 "file size: 600 lines, and 600 with no final newline, pass" "$r" 20-file-size.sh
for f in dotnet/Assembly.Domain/A.cs app/src/lib/B.svelte app/src/lib/C.ts shell/src/main.rs app/src/lib/résumé.ts; do
  seq 601 >"$r/$f"
  expect 1 "file size: 601 lines in $f fails" "$r" 20-file-size.sh
  seq 600 >"$r/$f"
done
rm -rf "$r"

r=$(repo_with 45-no-select-star.sh)
mkdir -p "$r/dotnet/Assembly.Application" "$r/app/src/lib"
echo '// One result row, read by column position. So every query names its columns: never SELECT *.' >"$r/dotnet/Assembly.Application/IRow.cs"
echo 'const string All = "SELECT key, value FROM preferences";' >"$r/dotnet/Assembly.Application/A.cs"
echo 'const all = "SELECT * FROM notes";' >"$r/app/src/lib/a.ts"
expect 0 "no select star: named columns, a comment saying SELECT *, and code outside dotnet/ pass" "$r" 45-no-select-star.sh
for bad in 'const string All = "SELECT * FROM preferences";' 'var sql = "select  *  from rows WHERE seq > 1";'; do
  echo "$bad" >"$r/dotnet/Assembly.Application/A.cs"
  expect 1 "no select star: fails on: $bad" "$r" 45-no-select-star.sh
done
rm -rf "$r"

# The two .NET checks fail on a probe in a scratch copy. In the commit hook (LINT_STAGED_ONLY=1) each skips a commit
# with no .NET file in it, and runs on a .cs change.
r=$(dotnet_repo_with 50-dotnet-format.sh)
expect 0 "dotnet format: the committed tree passes" "$r" 50-dotnet-format.sh
printf 'namespace Assembly.Domain;\n\npublic static class FormatProbe\n{\n        public static int One() => 1;\n}\n' \
  >"$r/dotnet/Assembly.Domain/FormatProbe.cs"
expect 1 "dotnet format: a badly indented file fails" "$r" 50-dotnet-format.sh
commit_all "$r"
echo note >"$r/notes.md"
LINT_STAGED_ONLY=1 expect 0 "dotnet format: the hook skips a commit with nothing under dotnet/" "$r" 50-dotnet-format.sh
echo '// touched' >>"$r/dotnet/Assembly.Domain/FormatProbe.cs"
LINT_STAGED_ONLY=1 expect 1 "dotnet format: the hook runs on a .cs change" "$r" 50-dotnet-format.sh
rm -rf "$r"

r=$(dotnet_repo_with 70-architecture.sh)
expect 0 "architecture: the committed tree passes" "$r" 70-architecture.sh
printf 'namespace Assembly.Application;\n\npublic static class ArchitectureProbe\n{\n    public static string Read() => System.IO.File.ReadAllText("x");\n}\n' \
  >"$r/dotnet/Assembly.Application/ArchitectureProbe.cs"
expect 1 "architecture: the core opening a file fails" "$r" 70-architecture.sh
commit_all "$r"
echo note >"$r/notes.md"
LINT_STAGED_ONLY=1 expect 0 "architecture: the hook skips a commit with nothing under dotnet/" "$r" 70-architecture.sh
echo '// touched' >>"$r/dotnet/Assembly.Application/ArchitectureProbe.cs"
LINT_STAGED_ONLY=1 expect 1 "architecture: the hook runs on a .cs change" "$r" 70-architecture.sh
rm -rf "$r"

# scripts/lint.sh runs every lint.d check and fails if any one does.
r=$(mktemp -d)
git -C "$r" init -q
mkdir -p "$r/scripts/lint.d"
cp "$here/lint.sh" "$r/scripts/"
printf 'exit 0\n' >"$r/scripts/lint.d/10-passes.sh"
printf 'exit 1\n' >"$r/scripts/lint.d/20-fails.sh"
printf 'exit 0\n' >"$r/scripts/lint.d/30-passes.sh"
bash "$r/scripts/lint.sh" >/dev/null 2>&1 && got=0 || got=$?
if [ "$got" = 1 ]; then echo "ok   lint.sh: one failing check fails it"; else echo "FAIL lint.sh: one failing check fails it: exit $got, wanted 1"; failed=1; fi
rm "$r/scripts/lint.d/20-fails.sh"
bash "$r/scripts/lint.sh" >/dev/null 2>&1 && got=0 || got=$?
if [ "$got" = 0 ]; then echo "ok   lint.sh: every check passing passes it"; else echo "FAIL lint.sh: every check passing passes it: exit $got, wanted 0"; failed=1; fi
rm -rf "$r"

exit "$failed"
