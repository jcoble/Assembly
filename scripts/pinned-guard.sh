#!/usr/bin/env bash
# Claude Code hook (.claude/settings.json, before Edit/Write/MultiEdit/NotebookEdit): a locked file
# (scripts/locked-paths.txt) may be written and refined on a branch until it lands, but one already on origin/main
# may not be changed. An OK goes in .pinned-approvals (scripts/check-pinned.sh).
# It fails closed: without jq, with input it can't read, or with no origin/main to check a locked path against, the
# edit is refused. A file outside a checkout with a locked-paths list isn't locked.
command -v jq >/dev/null || { echo "pinned-guard: jq isn't installed, so no edit can be checked; refusing" >&2; exit 2; }
path=$(jq -er '.tool_input.file_path // .tool_input.notebook_path') \
  || { echo "pinned-guard: couldn't read the edit's file path; refusing" >&2; exit 2; }
dir=$(dirname "$path")
while [ ! -d "$dir" ]; do dir=$(dirname "$dir"); done
top=$(git -C "$dir" rev-parse --show-toplevel 2>/dev/null) || exit 0
rel=${path#"$top"/}
# The locked paths are those listed on origin/main or in this checkout, so dropping a line unlocks nothing.
globs=$({ git -C "$top" show origin/main:scripts/locked-paths.txt 2>/dev/null; cat "$top/scripts/locked-paths.txt" 2>/dev/null; } \
  | grep -vE '^\s*(#|$)' | sort -u)
locked=""
while IFS= read -r glob; do
  [ -n "$glob" ] && [[ $rel == $glob ]] && { locked=$glob; break; }
done <<<"$globs"
[ -n "$locked" ] || exit 0
git -C "$top" rev-parse -q --verify origin/main >/dev/null \
  || { echo "pinned-guard: no origin/main in $top to check $path against; refusing" >&2; exit 2; }
if git -C "$top" cat-file -e "origin/main:$rel" 2>/dev/null; then
  echo "$path is locked ($locked in scripts/locked-paths.txt): changing it needs an OK from Jesse or the move thread (AGENTS.md, \"Who may OK\"). Stop and ask." >&2
  exit 2
fi
exit 0
