#!/usr/bin/env bash
# Claude Code hook (.claude/settings.json, before Edit/Write/MultiEdit/NotebookEdit): a locked file (a pinned test,
# app/scripts/guards.sh, app/scripts/guards-allowed.txt) may be written and refined on a branch until it lands, but
# one already on origin/main may not be changed. An OK goes in .pinned-approvals (scripts/check-pinned.sh).
# It fails closed: without jq or git, with input it can't read, or with no origin/main to check, the edit is refused.
command -v jq >/dev/null || { echo "pinned-guard: jq isn't installed, so no edit can be checked; refusing" >&2; exit 2; }
path=$(jq -er '.tool_input.file_path // .tool_input.notebook_path') \
  || { echo "pinned-guard: couldn't read the edit's file path; refusing" >&2; exit 2; }
case "$path" in
  */Assembly.Tests/Pinned/*|*/app/test/pinned/*|*/app/scripts/guards.sh|*/app/scripts/guards-allowed.txt) ;;
  *) exit 0 ;;
esac
dir=$(dirname "$path")
while [ ! -d "$dir" ]; do dir=$(dirname "$dir"); done
top=$(git -C "$dir" rev-parse --show-toplevel 2>/dev/null) \
  || { echo "pinned-guard: $path isn't in a git checkout, so it can't be checked; refusing" >&2; exit 2; }
git -C "$top" rev-parse -q --verify origin/main >/dev/null \
  || { echo "pinned-guard: no origin/main in $top to check $path against; refusing" >&2; exit 2; }
if git -C "$top" cat-file -e "origin/main:${path#"$top"/}" 2>/dev/null; then
  echo "$path is locked like a pinned test: changing it needs an OK from Jesse or the move thread (AGENTS.md, \"Who may OK\"). Stop and ask." >&2
  exit 2
fi
exit 0
