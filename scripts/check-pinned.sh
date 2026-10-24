#!/usr/bin/env bash
# Pinned tests (dotnet/Assembly.Tests/Pinned/, app/test/pinned/) may be added freely, but changing or deleting one
# needs Jesse's OK: a line "<blob id or deleted> <path>" in .pinned-approvals.
#   scripts/check-pinned.sh [base] [head]   fails if a commit in base..head (default origin/main..HEAD) changes or
#                                           deletes a pinned file without an OK in head's .pinned-approvals
#   scripts/check-pinned.sh approve <path>  prints the OK line for the file as it is now (for Jesse to add)
set -euo pipefail
cd "$(dirname "$0")/.."
pinned=(dotnet/Assembly.Tests/Pinned app/test/pinned)

if [ "${1:-}" = approve ]; then
  path=${2:?usage: scripts/check-pinned.sh approve <path>}
  if [ -e "$path" ]; then echo "$(git hash-object "$path") $path"; else echo "deleted $path"; fi
  exit
fi

base=${1:-origin/main} head=${2:-HEAD}
approvals=$(git show "$head:.pinned-approvals" 2>/dev/null || true)
failed=0
while IFS=$'\t' read -r status path; do
  case "$status" in
    A) continue ;;
    D) id=deleted ;;
    *) id=$(git rev-parse "$head:$path") ;;
  esac
  if ! grep -qxF "$id $path" <<<"$approvals"; then
    echo "pinned file changed without Jesse's OK: $path ($status)" >&2
    failed=1
  fi
done < <(git diff --no-renames --name-status "$base" "$head" -- "${pinned[@]}")
[ "$failed" = 0 ] && echo "pinned tests: no unapproved changes in $base..$head"
exit "$failed"
