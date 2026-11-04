#!/usr/bin/env bash
# Tests for the checks in scripts/lint.d/: each one runs in a throwaway repo, fails on a bad sample and passes once
# the sample is fixed. Run: bash scripts/lint-checks.test.sh
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
failed=0
source_repo=$(git -C "$here/.." rev-parse --show-toplevel) || exit 1

# The controller stages authorized source before running; never silently test an older tree.
source_ready() {
  local unmerged untracked
  unmerged=$(git -C "$source_repo" ls-files --unmerged) || return 1
  if [ -n "$unmerged" ]; then
    echo "lint fixtures: conflicted index; resolve before validating source" >&2
    return 1
  fi
  if ! git -C "$source_repo" diff --quiet --; then
    echo "lint fixtures: unstaged source; stage intended authorized changes first" >&2
    return 1
  fi
  untracked=$(git -C "$source_repo" ls-files --others --exclude-standard) || return 1
  if [ -n "$untracked" ]; then
    echo "lint fixtures: untracked source; account for intended files before validation" >&2
    return 1
  fi
}
source_ready || exit 1
source_head=$(git -C "$source_repo" rev-parse HEAD) || exit 1
source_tree=$(git -C "$source_repo" write-tree) || exit 1
printf 'lint fixtures: HEAD commit %s; tested index tree %s\n' "$source_head" "$source_tree"

finish() {
  local result=$? current_tree
  trap - EXIT
  if ! source_ready; then
    result=1
  else
    current_tree=$(git -C "$source_repo" write-tree) || result=1
    if [ "${current_tree:-}" != "$source_tree" ] || [ "$(git -C "$source_repo" rev-parse HEAD)" != "$source_head" ]; then
      echo "lint fixtures: source/index changed during validation; recorded tree is stale" >&2
      result=1
    fi
  fi
  exit "$result"
}
trap finish EXIT

# A throwaway repo holding one lint.d script; prints its path.
repo_with() {
  local repo
  repo=$(mktemp -d)
  git -C "$repo" init -q
  mkdir -p "$repo/scripts/lint.d"
  cp "$here/lint.d/$1" "$repo/scripts/lint.d/"
  echo "$repo"
}
# Archive .NET source from the recorded prospective tree, including its root helper and Assembly.slnx.
# The retained desktop/src marker lets the architecture tests locate this scratch root. Building Infrastructure stamps
# its backend identity from HEAD with the two identity scripts, so the scratch repo needs both and a first commit.
dotnet_repo_with() {
  local repo
  repo=$(repo_with "$1")
  git -C "$source_repo" archive "$source_tree" dotnet scripts/dev-remote-identity.ts scripts/dev-remote-process.ts | tar -x -C "$repo" || return 1
  mkdir -p "$repo/desktop/src"
  commit_all "$repo" || return 1
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

r=$(repo_with 30-provider-branches.sh)
mkdir -p "$r/desktop/src" "$r/dotnet/Assembly.Application/Common/Acp/V1" "$r/dotnet/Assembly.Application/Common/Acp/V2" "$r/dotnet/Assembly.Tests" "$r/desktop/src/core"
echo "const agent = pick(); // a comment that says claude is fine" >"$r/desktop/src/a.rs"
echo 'if (name == "claude") { }' >"$r/dotnet/Assembly.Application/Common/Acp/ProviderGlyph.cs"
echo 'Assert.True(kind == "codex");' >"$r/dotnet/Assembly.Tests/T.cs"
expect 0 "provider branches: the exact ProviderGlyph.cs and tests pass" "$r" 30-provider-branches.sh
for neighbor in Reader.cs V1/Reader.cs V2/Reader.cs; do
  echo 'if (name == "claude") { }' >"$r/dotnet/Assembly.Application/Common/Acp/$neighbor"
  expect 1 "provider branches: neighboring Acp/$neighbor is not exempt" "$r" 30-provider-branches.sh
  rm "$r/dotnet/Assembly.Application/Common/Acp/$neighbor"
done
for bad in "if (agent === 'claude') go();" "case 'codex': break;" "x = agent == \"antigravity\" ? a : b" \
  "Agent::Claude => run()," "if matches!(a, Agent::Codex) {}" "const label = 'codex' !== agent"; do
  echo "$bad" >"$r/desktop/src/a.rs"
  expect 1 "provider branches: fails on: $bad" "$r" 30-provider-branches.sh
done
rm -rf "$r"

r=$(repo_with 20-file-size.sh)
mkdir -p "$r/dotnet/Assembly.Domain" "$r/desktop/src" "$r/desktop/node_modules/x" "$r/desktop/src/core"
seq 600 >"$r/dotnet/Assembly.Domain/A.cs"
printf '%s\n' $(seq 599) >"$r/desktop/src/B.rs" && printf 'last' >>"$r/desktop/src/B.rs"
seq 900 >"$r/desktop/node_modules/x/big.ts"
seq 900 >"$r/desktop/src/notes.md"
expect 0 "file size: 600 lines, and 600 with no final newline, pass" "$r" 20-file-size.sh
for f in dotnet/Assembly.Domain/A.cs desktop/src/B.rs desktop/src/C.rs desktop/src/core/main.rs desktop/src/résumé.rs; do
  seq 601 >"$r/$f"
  expect 1 "file size: 601 lines in $f fails" "$r" 20-file-size.sh
  seq 600 >"$r/$f"
done
rm -rf "$r"

r=$(repo_with 45-no-select-star.sh)
mkdir -p "$r/dotnet/Assembly.Application" "$r/desktop/src"
echo '// One result row, read by column position. So every query names its columns: never SELECT *.' >"$r/dotnet/Assembly.Application/IRow.cs"
echo 'const string All = "SELECT key, value FROM preferences";' >"$r/dotnet/Assembly.Application/A.cs"
echo 'const all = "SELECT * FROM notes";' >"$r/desktop/src/a.rs"
expect 0 "no select star: named columns, a comment saying SELECT *, and code outside dotnet/ pass" "$r" 45-no-select-star.sh
for bad in 'const string All = "SELECT * FROM preferences";' 'var sql = "select  *  from rows WHERE seq > 1";'; do
  echo "$bad" >"$r/dotnet/Assembly.Application/A.cs"
  expect 1 "no select star: fails on: $bad" "$r" 45-no-select-star.sh
done
rm -rf "$r"

r=$(repo_with 80-no-bare-task-starts.sh)
mkdir -p "$r/dotnet/Assembly.Application" "$r/dotnet/Assembly.Tests"
printf '%s\n' 'var task = Task.Run(Work);' '_ = kill(-pid, 9);' 'work.Start("clock", TickAsync);' '// _ = RunAsync(); in a comment' \
  >"$r/dotnet/Assembly.Application/A.cs"
printf '%s\n' '// Never use async void.' '    /// No async void here.' '     * nor async void here' '    _ => RunAsync(),' \
  >"$r/dotnet/Assembly.Application/B.cs"
expect 0 "bare task starts: held tasks, a discarded non-task call, comments and a lambda pass" "$r" 80-no-bare-task-starts.sh
rm "$r/dotnet/Assembly.Application/B.cs"
for bad in '_ = RunAsync();' '_=RunAsync();' '_ = RunAsync ();' '    _ = Task.Run(Work);' '_ = write.ContinueWith(_ => { });' 'Task.Run(Work);' \
  'Task.Factory.StartNew(Work);' 'ThreadPool.UnsafeQueueUserWorkItem(Work, null);' 'new Thread(Work).Start();' 'private async void OnClick() { }'; do
  echo "$bad" >"$r/dotnet/Assembly.Tests/T.cs"
  expect 1 "bare task starts: fails on: $bad (tests too)" "$r" 80-no-bare-task-starts.sh
  rm "$r/dotnet/Assembly.Tests/T.cs"
done
rm -rf "$r"

# The two .NET checks fail on a probe in a scratch copy. In the commit hook (LINT_STAGED_ONLY=1) each skips a commit
# with no .NET file in it, and runs on a .cs change.
r=$(dotnet_repo_with 50-dotnet-format.sh) || exit 1
expect 0 "dotnet format: the prospective tree passes" "$r" 50-dotnet-format.sh
printf 'namespace Assembly.Domain;\n\npublic static class FormatProbe\n{\n        public static int One() => 1;\n}\n' \
  >"$r/dotnet/Assembly.Domain/FormatProbe.cs"
expect 1 "dotnet format: a badly indented file fails" "$r" 50-dotnet-format.sh
commit_all "$r"
echo note >"$r/notes.md"
LINT_STAGED_ONLY=1 expect 0 "dotnet format: the hook skips a commit with nothing under dotnet/" "$r" 50-dotnet-format.sh
echo '// touched' >>"$r/dotnet/Assembly.Domain/FormatProbe.cs"
LINT_STAGED_ONLY=1 expect 1 "dotnet format: the hook runs on a .cs change" "$r" 50-dotnet-format.sh
rm -rf "$r"

r=$(dotnet_repo_with 70-architecture.sh) || exit 1
expect 0 "architecture: the prospective tree passes" "$r" 70-architecture.sh
printf 'namespace Assembly.Application;\n\npublic static class ArchitectureProbe\n{\n    public static string Read() => System.IO.File.ReadAllText("x");\n}\n' \
  >"$r/dotnet/Assembly.Application/ArchitectureProbe.cs"
expect 1 "architecture: the core opening a file fails" "$r" 70-architecture.sh
commit_all "$r"
echo note >"$r/notes.md"
LINT_STAGED_ONLY=1 expect 0 "architecture: the hook skips a commit with nothing under dotnet/" "$r" 70-architecture.sh
echo '// touched' >>"$r/dotnet/Assembly.Application/ArchitectureProbe.cs"
LINT_STAGED_ONLY=1 expect 1 "architecture: the hook runs on a .cs change" "$r" 70-architecture.sh
rm -rf "$r"

# A filter that matches no test is a failure, not a pass with nothing run.
r=$(dotnet_repo_with 70-architecture.sh) || exit 1
sed -i 's/^namespace Assembly.Tests.Architecture;/namespace Assembly.Tests.Moved;/' "$r"/dotnet/Assembly.Tests/Architecture/*.cs
expect 1 "architecture: no architecture test found fails" "$r" 70-architecture.sh
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
