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
git config core.hooksPath /dev/null
git config commit.gpgsign false
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
expect 1 "a fix with no test change stops it" work $'fix(chat): rows keep order\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/x.rs
expect 0 "a fix that changes a .NET test passes" work $'fix(chat): rows keep order\n\nArchitecture: unchanged\nCommitted-by: Claude' dotnet/Assembly.Tests/X.cs
expect 1 "a current app test does not count as a retained test" work $'Fix rows\n\nArchitecture: unchanged\nCommitted-by: Claude' app/test/x.test.ts
expect 0 "a fix that changes only a script test passes" work $'fix: guard\n\nArchitecture: unchanged\nCommitted-by: Claude' scripts/x.test.sh
expect 1 "a fix that changes only a script that isn't a test stops it" work $'fix: guard\n\nArchitecture: unchanged\nCommitted-by: Claude' scripts/x.sh
expect 0 "a fix that changes only a TypeScript script test passes" work $'fix: guard\n\nArchitecture: unchanged\nCommitted-by: Claude' scripts/x.test.ts
expect 0 "a fix that changes only a nested script test passes" work $'fix: guard\n\nArchitecture: unchanged\nCommitted-by: Claude' scripts/verify-pr/x.test.ts
expect 1 "a fix that changes only a nested script stops" work $'fix: guard\n\nArchitecture: unchanged\nCommitted-by: Claude' scripts/verify-pr/x.ts
expect 0 "a fix that changes a Rust test file beside the code passes" work $'fix: frames\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/core/socket/frames_tests.rs
expect 0 "a fix that changes a Rust tests.rs beside the code passes" work $'fix: frames\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/core/socket/tests.rs
expect 0 "a fix that changes a Rust test under desktop/tests passes" work $'fix: frames\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/tests/x.rs
expect 1 "a fix that changes only Rust code stops it" work $'fix: frames\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/core/mod.rs
expect 0 "a fix with No-test passes" work $'fix: typo\n\nNo-test: a comment only\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/x.rs
expect 0 "a fix that changes a test with an accented name passes" work $'fix: names\n\nArchitecture: unchanged\nCommitted-by: Claude' 'dotnet/Assembly.Tests/résumé.cs'
expect 0 "a fix with No-test above a long body passes" work $'fix: x\n\nNo-test: r\n'"$(seq 20000)"$'\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/x.rs
expect 0 "a subject that only starts with fixup isn't a fix" work $'fixup the docs\n\nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/x.rs
# Test changes need no TEST-CHANGE line: the edit hook stops agents, and commits and pushes never refuse a test change.
mkdir -p desktop/src && printf 'fn keeps() {\n assert_eq!(a, 1);\n}\n' >desktop/src/view_tests.rs && git add desktop/src/view_tests.rs
git -c user.name=t -c user.email=t@t commit -q -m 'seed a test'
printf 'fn keeps() {\n assert_eq!(a, 2);\n}\n' >desktop/src/view_tests.rs && git add desktop/src/view_tests.rs
expect 0 "a test-only commit changing an assertion, with no TEST-CHANGE line, passes" work "$ok"
git rm -q desktop/src/view_tests.rs
expect 0 "a commit deleting a test, with no TEST-CHANGE line, passes" work "$ok"
expect 1 "a message without Architecture: unchanged stops it" work $'Subject\n\nCommitted-by: Claude'
expect 0 "Architecture: unchanged in any case passes" work $'Subject\n\narchitecture: Unchanged\nCommitted-by: Claude'
expect 1 "changing the old architecture copy still needs an acknowledgement" work $'Subject\n\nCommitted-by: Claude' docs/architecture.md
expect 1 "a file only named like the guide doesn't count as it" work $'Subject\n\nCommitted-by: Claude' docs/architectureXmd

architecture=https://app.notion.com/p/409394b0689d8184906cd246dc89acd1
expect 0 "the exact canonical architecture URL passes" work $'Subject\n\nArchitecture: updated '"$architecture"$'\nCommitted-by: Claude'
expect 1 "updated without a URL stops it" work $'Subject\n\nArchitecture: updated\nCommitted-by: Claude'
expect 1 "another Notion URL stops it" work $'Subject\n\nArchitecture: updated https://app.notion.com/p/'"$page"$'\nCommitted-by: Claude'
expect 1 "a malformed canonical URL stops it" work $'Subject\n\nArchitecture: updated app.notion.com/p/409394b0689d8184906cd246dc89acd1\nCommitted-by: Claude'
expect 1 "a canonical URL with a suffix stops it" work $'Subject\n\nArchitecture: updated '"$architecture"$'?x=1\nCommitted-by: Claude'
expect 1 "an unchanged prefix with trailing prose stops it" work $'Subject\n\nArchitecture: unchanged maybe\nCommitted-by: Claude'
expect 1 "an empty No-test reason stops it" work $'fix: x\n\nNo-test:  \nArchitecture: unchanged\nCommitted-by: Claude' desktop/src/x.rs
expect 1 "a malformed task URL stops it" tsk-42-thing $'Subject\n\nTask: TSK-42 https://app.notion.com/p/no-page\nArchitecture: unchanged\nCommitted-by: Claude'

# A commit GitHub made in its web UI (committer noreply@github.com) can't carry a Committed-by line; CI skips it, saying so.
git checkout -q -B work
GIT_COMMITTER_NAME=GitHub GIT_COMMITTER_EMAIL=noreply@github.com git -c user.name=t -c user.email=t@t commit -q --allow-empty -m 'Revert "x"'
web=$("$check" --commit HEAD work 2>/dev/null)
ci=$?
if [ "$ci" = 0 ] && grep -q "skipped" <<<"$web"; then echo "ok   a GitHub web UI commit is skipped, and says so"
else echo "FAIL web UI commit: ci $ci, output: $web"; failed=1; fi
git -c user.name=t -c user.email=t@t commit -q --allow-empty -m 'Revert "x"'
"$check" --commit HEAD work 2>/dev/null
ci=$?
if [ "$ci" = 1 ]; then echo "ok   the same message from another committer still stops"
else echo "FAIL other committer: ci $ci, wanted 1"; failed=1; fi

# CI checks a commit made before de5a7b0e (2026-11-03T02:01:28Z) against the rule it replaced: the commit changes
# docs/architecture.md, or a line starts with Architecture: unchanged.
# ci_dated <committer date> <message> [file to change]: prints the --commit exit code
ci_dated() {
  local when=$1 msg=$2 f=${3:-}
  git checkout -q -B work
  if [ -n "$f" ]; then mkdir -p "$(dirname "$f")"; echo "$RANDOM" >"$f"; git add "$f"; fi
  GIT_COMMITTER_DATE=$when git -c user.name=t -c user.email=t@t commit -q --allow-empty -m "$msg"
  "$check" --commit HEAD work >/dev/null 2>&1
  echo $?
}
before=2026-11-03T02:01:27Z cutoff=2026-11-03T02:01:28Z
bare=$'Subject\n\nArchitecture: updated\nCommitted-by: Claude'
linked=$'Subject\n\nArchitecture: unchanged \xe2\x80\x94 https://app.notion.com/p/'"$page"$'\nCommitted-by: Claude'
ci=$(ci_dated "$before" "$bare" docs/architecture.md)
if [ "$ci" = 0 ]; then echo "ok   an old commit changing docs/architecture.md passes with a bare Architecture: updated"
else echo "FAIL old bare updated with the doc: ci $ci, wanted 0"; failed=1; fi
ci=$(ci_dated "$before" "$linked")
if [ "$ci" = 0 ]; then echo "ok   an old commit passes with Architecture: unchanged and trailing text"
else echo "FAIL old unchanged with trailing text: ci $ci, wanted 0"; failed=1; fi
ci=$(ci_dated "$cutoff" "$bare" docs/architecture.md)$(ci_dated "$cutoff" "$linked")
if [ "$ci" = 11 ]; then echo "ok   a commit at the cutoff is held to today's rule"
else echo "FAIL commit at the cutoff: ci $ci, wanted 11"; failed=1; fi
ci=$(ci_dated "$before" $'Subject\n\nCommitted-by: Claude')
if [ "$ci" = 1 ]; then echo "ok   an old commit with no Architecture line and no doc change stops"
else echo "FAIL old commit with no Architecture line: ci $ci, wanted 1"; failed=1; fi
printf '%s\n' "$bare" >"$repo/.msg"
"$check" "$repo/.msg" 2>/dev/null
hook=$?
if [ "$hook" = 1 ]; then echo "ok   the hook still stops a bare Architecture: updated"
else echo "FAIL hook bare Architecture: updated: hook $hook, wanted 1"; failed=1; fi

# Historical commits retain app/test recognition, while the staged hook always requires retained tests.
mkdir -p app
printf '{}\n' >app/package.json
git add app/package.json
git -c user.name=t -c user.email=t@t commit -q -m 'historical app manifest'
printf 'test\n' >app/test/old.test.ts
git add app/test/old.test.ts
printf '%s\n' $'fix: historical app\n\nArchitecture: unchanged\nCommitted-by: Claude' >"$repo/.msg"
"$check" "$repo/.msg" 2>/dev/null
hook=$?
git -c user.name=t -c user.email=t@t commit -q -F "$repo/.msg"
"$check" --commit HEAD work 2>/dev/null
ci=$?
if [ "$hook" = 1 ] && [ "$ci" = 0 ]; then echo "ok   old app test counts only for a historical commit"
else echo "FAIL historical app test: hook $hook, ci $ci"; failed=1; fi

git rm -q app/package.json
git -c user.name=t -c user.email=t@t commit -q -m 'remove historical runtime'
expect 1 "old app tests no longer count after runtime removal" work $'fix: removed app\n\nArchitecture: unchanged\nCommitted-by: Claude' app/test/old.test.ts

exit "$failed"
