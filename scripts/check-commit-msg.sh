#!/usr/bin/env bash
# The commit-message rules, ported from mac-command-bar's commit-msg hook and commitChecks.ts --commit-msg:
#   scripts/check-commit-msg.sh <message file>          the hook (.githooks/commit-msg), on the staged files
#   scripts/check-commit-msg.sh --commit <id> <branch>  CI, on each of a pull request's own commits
# A message has no Co-Authored-By line and ends with `Committed-by: <name>`. On a tsk-<n> branch it has
# `Task: TSK-<n> https://app.notion.com/p/<page id>`. A fix commit (subject starting "fix") changes a test
# (dotnet/Assembly.Tests/, app/test/ or a scripts/*.test.sh) or says why not with a `No-test: <reason>` line. A commit
# that doesn't change docs/architecture.md says `Architecture: unchanged`.
set -euo pipefail

if [ "${1:-}" = --commit ]; then
  commit=${2:?usage: check-commit-msg.sh --commit <id> <branch>} branch=${3:-}
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

grep -qFx 'docs/architecture.md' <<<"$changed" || grep -Eiq '^Architecture: unchanged' <<<"$message" \
  || stop "update docs/architecture.md in this commit, or say Architecture: unchanged when it describes nothing this changes."

if head -n 1 <<<"$message" | grep -Eiq '^fix([^A-Za-z0-9_]|$)'; then
  grep -Eq '^(dotnet/Assembly\.Tests/|app/test/|scripts/[^/]+\.test\.sh$)' <<<"$changed" \
    || grep -Eq '^No-test:[[:space:]]*[^[:space:]]' <<<"$(tail -n +2 <<<"$message")" \
    || stop "a fix commit changes a test (dotnet/Assembly.Tests/, app/test/ or scripts/*.test.sh), or says why not with No-test: <reason>."
fi
exit 0
