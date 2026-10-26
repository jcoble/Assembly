#!/usr/bin/env bash
# Tests for scripts/pinned-guard.sh: feeds it hook input for edits to a locked file in a throwaway repo and checks
# whether it lets each one through (exit 0) or refuses it (exit 2). Run: bash scripts/pinned-guard.test.sh
set -uo pipefail
guard=$(cd "$(dirname "$0")" && pwd)/pinned-guard.sh
repo=$(mktemp -d)
trap 'rm -rf "$repo"' EXIT
git -C "$repo" init -q
mkdir "$repo/scripts"
printf 'locked.txt\nnew.txt\n' >"$repo/scripts/locked-paths.txt"
printf 'one two two\n' >"$repo/locked.txt"
printf 'free\n' >"$repo/free.txt"
git -C "$repo" add -A
git -C "$repo" -c user.name=t -c user.email=t@t commit -qm base
git -C "$repo" update-ref refs/remotes/origin/main HEAD

failed=0
blob() { printf '%s' "$1" | git hash-object --stdin; }
ok() { printf '%s\n' "$@" >"$repo/.pinned-approvals"; }
# expect <exit code> <name> <tool> <tool_input as JSON>
expect() {
  jq -n --arg tool "$3" --argjson input "$4" '{tool_name: $tool, tool_input: $input}' | "$guard" 2>/dev/null
  local got=$?
  if [ "$got" = "$1" ]; then echo "ok   $2"; else echo "FAIL $2: exit $got, wanted $1"; failed=1; fi
}
f=$repo/locked.txt

ok "$(blob 'new content') locked.txt by:jesse"
expect 0 "Write with a matching OK goes through" Write "$(jq -n --arg p "$f" '{file_path: $p, content: "new content"}')"
expect 2 "Write one byte different is refused" Write "$(jq -n --arg p "$f" '{file_path: $p, content: "new content!"}')"

ok "$(blob 'new content') locked.txt"
expect 2 "an OK line that doesn't say who approved is refused" Write "$(jq -n --arg p "$f" '{file_path: $p, content: "new content"}')"

ok "$(blob 'new content') other.txt by:jesse"
expect 2 "an OK line for another path is refused" Write "$(jq -n --arg p "$f" '{file_path: $p, content: "new content"}')"

ok "$(blob $'one three two\n') locked.txt by:move-thread renamed a helper"
expect 0 "Edit with a matching OK goes through" Edit "$(jq -n --arg p "$f" '{file_path: $p, old_string: "two two", new_string: "three two"}')"

ok "$(blob $'one 2 2\n') locked.txt by:jesse"
expect 2 "a non-unique old_string without replace_all is refused" Edit "$(jq -n --arg p "$f" '{file_path: $p, old_string: "two", new_string: "2"}')"
expect 0 "replace_all with a matching OK goes through" Edit "$(jq -n --arg p "$f" '{file_path: $p, old_string: "two", new_string: "2", replace_all: true}')"
expect 2 "an empty old_string is refused" Edit "$(jq -n --arg p "$f" '{file_path: $p, old_string: "", new_string: "2"}')"

ok "$(blob $'1 2 2\n') locked.txt by:jesse"
expect 0 "MultiEdit applies its edits in order" MultiEdit "$(jq -n --arg p "$f" '{file_path: $p, edits: [{old_string: "two", new_string: "2", replace_all: true}, {old_string: "one", new_string: "1"}]}')"

expect 2 "NotebookEdit on a locked file is refused" NotebookEdit "$(jq -n --arg p "$f" '{notebook_path: $p, new_source: "x"}')"

ok
expect 2 "a locked file with no OK line is refused" Write "$(jq -n --arg p "$f" '{file_path: $p, content: "new content"}')"
expect 0 "a locked file not yet on origin/main may be written" Write "$(jq -n --arg p "$repo/new.txt" '{file_path: $p, content: "x"}')"
expect 0 "a file that isn't locked may be written" Write "$(jq -n --arg p "$repo/free.txt" '{file_path: $p, content: "x"}')"

exit "$failed"
