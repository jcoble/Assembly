#!/usr/bin/env bash
# Small files (AGENTS.md, Code): fails, naming each one, if a tracked source file (.cs, .ts, .svelte, .rs) is over
# 600 lines. There's no baseline: a file over it is split. A last line without a newline counts, as in mac-command-bar.
cd "$(dirname "$0")/../.."
limit=600
failed=0
while IFS= read -r f; do
  n=$(awk 'END { print NR }' "$f")
  [ "$n" -gt "$limit" ] || continue
  echo "file size: $f has $n lines, over $limit; split it" >&2
  failed=1
done < <(git ls-files -- '*.cs' '*.ts' '*.svelte' '*.rs' \
  ':!**/node_modules/**' ':!**/target/**' ':!**/bin/**' ':!**/obj/**' ':!**/.svelte-kit/**' ':!**/build/**')
[ "$failed" = 0 ] && echo "file size: every source file is $limit lines or fewer"
exit "$failed"
