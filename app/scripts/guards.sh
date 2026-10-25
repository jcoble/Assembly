#!/usr/bin/env bash
# Plan rule 11 and the port's lag rules (AGENTS.md "Timers, ticks and :has() in the app"), on the page's source.
# Plain bash and grep, so CI needs nothing installed. Prints one line per hit and exits 1 if there are any.
# Locked (scripts/locked-paths.txt), like its allowlist.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

allowed=$(grep -v -e '^#' -e '^$' app/scripts/guards-allowed.txt || true)
is_allowed() { grep -qxF -- "$1" <<<"$allowed"; }

failed=0
# fail <files> <pattern> <why>: every line in <files> that matches <pattern>, skipping comment lines.
fail() {
  local files=$1 pattern=$2 why=$3 file hit
  for file in $files; do
    while IFS= read -r hit; do
      echo "$file:$hit  ($why)"
      failed=1
    done < <(grep -nE "$pattern" "$file" | grep -vE '^[0-9]+:[[:space:]]*(//|\*|/\*)' || true)
  done
}

source=$(git ls-files 'app/src/*.ts' 'app/src/*.svelte' 'app/src/*.css')
unlisted=$(for file in $source; do is_allowed "$file" || echo "$file"; done)
rows=$(echo "$source" | grep -E '^app/src/lib/chat/' || true)
markdown=$(echo "$source" | grep -vE '^app/src/lib/editor/preview/' || true)
# The shell's native calls live in lib/host/, one file each; Tauri is imported nowhere else.
outside_host=$(echo "$source" | grep -vE '^app/src/lib/host/' || true)

fail "$outside_host" "@tauri-apps/|[^.[:alnum:]_]listen\(" "Tauri only in app/src/lib/host/"
fail "$unlisted" "setInterval|setTimeout|requestAnimationFrame|(^|[^.[:alnum:]_])(delay|frame|debounce)\\(" "no timers or frame waits, raw or through the counted helpers, outside the allowlist"
fail "$unlisted" "new WebSocket\(" "sockets only through TrackedWebSocket"
fail "$unlisted" "(^|[^.[:alnum:]_])fetch\(" "fetches only in the allowlisted files (api.ts)"
fail "$unlisted" "(window|document)\.addEventListener\(" "window and document listeners only in the counted helpers (<svelte:window> is fine)"
fail "$rows" "(^|[^.[:alnum:]_])(tick|frame)\(|requestAnimationFrame" "no tick or frame waits in per-row components"
fail "$source" "observe\(document\.body" "no watcher on document.body"
fail "$markdown" "from ['\"](marked|markdown-it|micromark|remark[^'\"]*|mdast[^'\"]*|unified)['\"]" "markdown comes as blocks from the backend; only the editor preview parses it"
fail "$source" ":has\(" "no CSS :has()"

if [ "$failed" -ne 0 ]; then
  echo "guards: failed (app/scripts/guards.sh)"
  exit 1
fi
echo "guards: ok"
