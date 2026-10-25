#!/usr/bin/env bash
# Locked files (scripts/locked-paths.txt: the pinned tests, the app's guard script, lint, the list itself) may be
# added freely, but changing or deleting one needs
# an OK (AGENTS.md says who may give one): a line in .pinned-approvals naming the file's new content and who approved,
#   <blob id | deleted> <path> by:jesse
#   <blob id | deleted> <path> by:move-thread <the behavior list line, or why what the test checks is unchanged>
#   scripts/check-pinned.sh [base] [head]   fails if a commit in base..head (default origin/main..HEAD) changes or
#                                           deletes a pinned file without an OK in head's .pinned-approvals, or if
#                                           any OK line there doesn't say who approved
#   scripts/check-pinned.sh approve <path> jesse | move-thread <reason>   prints the OK line for the file as it is now
set -euo pipefail
cd "$(dirname "$0")/.."

if [ "${1:-}" = approve ]; then
  usage="usage: scripts/check-pinned.sh approve <path> jesse | move-thread <reason>"
  path=${2:?$usage} who=${3:?$usage}; shift 3
  case "$who" in
    jesse) by="by:jesse" ;;
    move-thread) [ $# -gt 0 ] || { echo "$usage" >&2; exit 2; }; by="by:move-thread $*" ;;
    *) echo "$usage" >&2; exit 2 ;;
  esac
  if [ -e "$path" ]; then echo "$(git hash-object "$path") $path $by"; else echo "deleted $path $by"; fi
  exit
fi

base=${1:-origin/main} head=${2:-HEAD}
approvals=$(git show "$head:.pinned-approvals" 2>/dev/null || true)
# The locked paths are those listed in base's or head's scripts/locked-paths.txt, so dropping a line unlocks nothing.
pinned=()
while IFS= read -r glob; do pinned+=(":(glob)$glob"); done < <(
  { git show "$base:scripts/locked-paths.txt" 2>/dev/null || true; git show "$head:scripts/locked-paths.txt" 2>/dev/null || true; } \
    | grep -vE '^\s*(#|$)' | sort -u)
[ ${#pinned[@]} -gt 0 ] || { echo "no scripts/locked-paths.txt in $base or $head" >&2; exit 1; }
failed=0
# Every OK line says who approved: by:jesse, or by:move-thread with its reason.
while IFS= read -r line; do
  if ! grep -qE '^[^ ]+ [^ ]+ (by:jesse|by:move-thread [^ ].*)$' <<<"$line"; then
    echo "OK line doesn't say who approved (by:jesse, or by:move-thread <reason>): $line" >&2
    failed=1
  fi
done < <(grep -vE '^\s*(#|$)' <<<"$approvals" || true)
while IFS=$'\t' read -r status path; do
  case "$status" in
    A) continue ;;
    D) id=deleted ;;
    *) id=$(git rev-parse "$head:$path") ;;
  esac
  if ! grep -qE "^$id $(sed 's/[.[\*^$+?(){}|/]/\\&/g' <<<"$path") (by:jesse|by:move-thread [^ ].*)$" <<<"$approvals"; then
    echo "pinned file changed without an OK: $path ($status)" >&2
    failed=1
  fi
done < <(git diff --no-renames --name-status "$base" "$head" -- "${pinned[@]}")
[ "$failed" = 0 ] && echo "pinned tests: no unapproved changes in $base..$head"
exit "$failed"
