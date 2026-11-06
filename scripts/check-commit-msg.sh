#!/usr/bin/env bash
# The commit-message rules, ported from mac-command-bar's commit-msg hook and commitChecks.ts --commit-msg:
#   scripts/check-commit-msg.sh <message file>          the hook (.githooks/commit-msg), on the staged files
#   scripts/check-commit-msg.sh --commit <id> <branch>  CI, on each of a pull request's own commits
# A message has no Co-Authored-By line and ends with `Committed-by: <name>`. On a tsk-<n> branch it has
# `Task: TSK-<n> https://app.notion.com/p/<page id>`. A fix commit (subject starting "fix") changes a test
# (dotnet/Assembly.Tests/, a *.test.sh or *.test.ts under scripts/, a tests.rs or *_tests.rs under desktop/src/, or
# desktop/tests/) or says why not with a `No-test: <reason>` line. Historical app/test paths count only in --commit mode
# for a commit containing the old app manifest. Architecture is unchanged or updated at the canonical Notion page.
# --commit skips a commit GitHub made in its web UI (committer noreply@github.com), which can't carry these lines.
set -euo pipefail

historical_app=false
if [ "${1:-}" = --commit ]; then
  commit=${2:?usage: check-commit-msg.sh --commit <id> <branch>} branch=${3:-}
  if [ "$(git log -1 --format=%ce "$commit")" = noreply@github.com ]; then
    echo "skipped $(git log -1 --format='%h %s' "$commit"): made in the GitHub web UI"; exit 0
  fi
  if git cat-file -e "$commit:app/package.json" 2>/dev/null; then historical_app=true; fi
  message=$(git log -1 --format=%B "$commit" | git stripspace --strip-comments)
  changed=$(git -c core.quotePath=false diff-tree --root --no-commit-id --name-only -r --diff-filter=ACMR "$commit")
else
  # Git writes merge messages itself.
  ! git rev-parse -q --verify MERGE_HEAD >/dev/null || exit 0
  message=$(git stripspace --strip-comments <"${1:?usage: check-commit-msg.sh <message file>}")
  branch=$(git symbolic-ref --quiet --short HEAD || true)
  changed=$(git -c core.quotePath=false diff --cached --name-only --diff-filter=ACMR)
fi

stop() { echo "commit stopped: $*" >&2; exit 1; }

! grep -Eiq '^Co-Authored-By:' <<<"$message" || stop "remove the Co-Authored-By trailer."
case "$(tail -n 1 <<<"$message")" in
  'Committed-by: '?*) ;;
  *) stop "the final message line must be Committed-by: <actual committer>." ;;
esac

case "$branch" in
  tsk-[0-9]*)
    task=$(sed -n 's/^tsk-\([0-9][0-9]*\).*$/\1/p' <<<"$branch")
    grep -Eq "^Task: TSK-$task https://app[.]notion[.]com/p/[0-9a-fA-F]{32}([?].*)?$" <<<"$message" \
      || stop "add Task: TSK-$task https://app.notion.com/p/<task-page-id>."
    ;;
esac

grep -Eiq '^Architecture: unchanged$' <<<"$message" \
  || grep -qFx 'Architecture: updated https://app.notion.com/p/409394b0689d8184906cd246dc89acd1' <<<"$message" \
  || stop "add Architecture: unchanged, or Architecture: updated https://app.notion.com/p/409394b0689d8184906cd246dc89acd1."

if head -n 1 <<<"$message" | grep -Eiq '^fix([^A-Za-z0-9_]|$)'; then
  grep -Eq '^(dotnet/Assembly\.Tests/|scripts/(.*/)?[^/]+\.test\.(sh|ts)$|desktop/src/(.*/)?([^/]*_)?tests\.rs$|desktop/tests/)' <<<"$changed" \
    || { [ "$historical_app" = true ] && grep -q '^app/test/' <<<"$changed"; } \
    || grep -Eq '^No-test:[[:space:]]*[^[:space:]]' <<<"$(tail -n +2 <<<"$message")" \
    || stop "a fix commit changes a test (dotnet/Assembly.Tests/, a *.test.sh or *.test.ts under scripts/, a tests.rs or *_tests.rs under desktop/src/, or desktop/tests/), or says why not with No-test: <reason>."
fi
hook_dir="$(cd "$(dirname "$0")/hooks" && pwd)"
if [ -n "${commit:-}" ]; then
  node "$hook_dir/test-integrity.ts" --commit "$commit"
  node "$hook_dir/staged-policy.ts" --commit "$commit"
else
  node "$hook_dir/test-integrity.ts" "$1"
fi
exit 0
