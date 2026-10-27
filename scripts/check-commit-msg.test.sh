#!/usr/bin/env bash
# Tests for scripts/check-commit-msg.sh: runs it on messages in a throwaway repo, in both of its modes (the hook's
# message file with staged files, and a commit id as CI uses it), and checks it passes (exit 0) or stops (exit 1).
# Run: bash scripts/check-commit-msg.test.sh
set -uo pipefail
check=$(cd "$(dirname "$0")" && pwd)/check-commit-msg.sh
repo=$(mktemp -d)
trap 'rm -rf "$repo"' EXIT
cd "$repo"
git init -q -b main
git -c user.name=t -c user.email=t@t commit -q --allow-empty -m base

failed=0
ok=$'Subject\n\nBody.\n\nArchitecture: unchanged\nCommitted-by: Claude'
page=0123456789abcdef0123456789ABCDEF
# expect <exit code> <name> <branch> <message> [file to stage]...
expect() {
  local want=$1 name=$2 branch=$3 msg=$4; shift 4
  git checkout -q -B "$branch"
  local f
  for f in "$@"; do mkdir -p "$(dirname "$f")"; echo "$RANDOM" >"$f"; git add "$f"; done
  printf '%s\n' "$msg" >"$repo/.msg"
  "$check" "$repo/.msg" 2>/dev/null
  local hook=$?
  git -c user.name=t -c user.email=t@t commit -q --allow-empty -F "$repo/.msg"
  "$check" --commit HEAD "$branch" 2>/dev/null
  local ci=$?
  if [ "$hook" = "$want" ] && [ "$ci" = "$want" ]; then echo "ok   $name"
  else echo "FAIL $name: hook $hook, ci $ci, wanted $want"; failed=1; fi
}

expect 0 "a message ending in Committed-by passes" work "$ok"
expect 1 "a Co-Authored-By line stops it" work $'Subject\n\nCo-authored-by: X <x@x>\nArchitecture: unchanged\nCommitted-by: Claude'
expect 1 "a last line other than Committed-by stops it" work $'Subject\n\nCommitted-by: Claude\nMore.'
expect 1 "an empty Committed-by stops it" work $'Subject\n\nCommitted-by: '
expect 0 "a tsk branch with its Task line passes" tsk-42-thing $'Subject\n\nTask: TSK-42 https://app.notion.com/p/'"$page"$'\nArchitecture: unchanged\nCommitted-by: Claude'
expect 1 "a tsk branch without its Task line stops it" tsk-42-thing "$ok"
expect 1 "a Task line for another task stops it" tsk-42-thing $'Subject\n\nTask: TSK-43 https://app.notion.com/p/'"$page"$'\nArchitecture: unchanged\nCommitted-by: Claude'
expect 1 "a fix with no test change stops it" work $'fix(chat): rows keep order\n\nArchitecture: unchanged\nCommitted-by: Claude' app/src/x.ts
expect 0 "a fix that changes a .NET test passes" work $'fix(chat): rows keep order\n\nArchitecture: unchanged\nCommitted-by: Claude' dotnet/Assembly.Tests/X.cs
expect 0 "a fix that changes an app test passes" work $'Fix rows\n\nArchitecture: unchanged\nCommitted-by: Claude' app/test/x.test.ts
expect 0 "a fix that changes only a script test passes" work $'fix: guard\n\nArchitecture: unchanged\nCommitted-by: Claude' scripts/x.test.sh
expect 1 "a fix that changes only a script that isn't a test stops it" work $'fix: guard\n\nArchitecture: unchanged\nCommitted-by: Claude' scripts/x.sh
expect 0 "a fix that changes a Rust test file beside the code passes" work $'fix: frames\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/core/socket/frames_tests.rs
expect 0 "a fix that changes a Rust tests.rs beside the code passes" work $'fix: frames\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/core/socket/tests.rs
expect 0 "a fix that changes a Rust test under desktop/tests passes" work $'fix: frames\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/tests/x.rs
expect 1 "a fix that changes only Rust code stops it" work $'fix: frames\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/core/mod.rs
expect 0 "a fix with No-test passes" work $'fix: typo\n\nNo-test: a comment only\nArchitecture: unchanged\nCommitted-by: Claude' app/src/x.ts
expect 0 "a fix that changes a test with an accented name passes" work $'fix: names\n\nArchitecture: unchanged\nCommitted-by: Claude' 'app/test/résumé.test.ts'
expect 0 "a fix with No-test above a long body passes" work $'fix: x\n\nNo-test: r\n'"$(seq 20000)"$'\nArchitecture: unchanged\nCommitted-by: Claude' app/src/x.ts
expect 0 "a subject that only starts with fixup isn't a fix" work $'fixup the docs\n\nArchitecture: unchanged\nCommitted-by: Claude' app/src/x.ts
expect 1 "a message without Architecture: unchanged stops it" work $'Subject\n\nCommitted-by: Claude'
expect 0 "Architecture: unchanged in any case passes" work $'Subject\n\narchitecture: Unchanged\nCommitted-by: Claude'
expect 0 "a commit that changes docs/architecture.md needs no Architecture line" work $'Subject\n\nCommitted-by: Claude' docs/architecture.md
expect 1 "a file only named like the guide doesn't count as it" work $'Subject\n\nCommitted-by: Claude' docs/architectureXmd

exit "$failed"
