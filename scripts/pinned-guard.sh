#!/usr/bin/env bash
# Claude Code hook (.claude/settings.json, before Edit/Write/MultiEdit/NotebookEdit): a locked file
# (scripts/locked-paths.txt) may be written and refined on a branch until it lands, but one already on origin/main
# may be changed only to content with an OK line in the checkout's .pinned-approvals (scripts/check-pinned.sh).
# It fails closed: without jq, with input it can't read, with no origin/main to check a locked path against, or with
# an edit whose result it can't compute exactly (a NotebookEdit, an old_string that isn't found exactly once without
# replace_all), the edit is refused. A file outside a checkout with a locked-paths list isn't locked.
command -v jq >/dev/null || { echo "pinned-guard: jq isn't installed, so no edit can be checked; refusing" >&2; exit 2; }
input=$(cat)
path=$(jq -er '.tool_input.file_path // .tool_input.notebook_path' <<<"$input") \
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
git -C "$top" cat-file -e "origin/main:$rel" 2>/dev/null || exit 0
# Who may OK: the same pattern scripts/check-pinned.sh checks.
who=$(sed -n "s/^who='\(.*\)'$/\1/p" "$(dirname "$0")/check-pinned.sh")
[ -n "$who" ] || { echo "pinned-guard: no who= line in $(dirname "$0")/check-pinned.sh; refusing" >&2; exit 2; }
cur=$path; [ -f "$cur" ] || cur=/dev/null
# The file's content after the edit, hashed as git would store it.
id=$(set -o pipefail; jq -j --rawfile cur "$cur" '
  def edit($e):
    if ($e.old_string | type) != "string" or $e.old_string == "" or ($e.new_string | type) != "string"
    then error("not an edit") end
    | split($e.old_string) as $parts
    | if ($parts | length) < 2 or ($e.replace_all != true and ($parts | length) != 2)
      then error("old_string is not found exactly once") end
    | $parts | join($e.new_string);
  .tool_input as $t
  | if .tool_name == "Write" then $t.content
    elif .tool_name == "Edit" then $cur | edit($t)
    elif .tool_name == "MultiEdit" then reduce $t.edits[] as $e ($cur; edit($e))
    else error("cannot compute the result") end
  | if type == "string" then . else error("no content") end' <<<"$input" 2>/dev/null | git hash-object --stdin) && \
  grep -qE "^$id $(sed 's/[.[\*^$+?(){}|/]/\\&/g' <<<"$rel") $who" "$top/.pinned-approvals" 2>/dev/null \
  && exit 0
echo "$path is locked ($locked in scripts/locked-paths.txt): changing it needs an OK line for its new content in .pinned-approvals, from Jesse or the move thread (AGENTS.md, \"Who may OK\"). Stop and ask." >&2
exit 2
