#!/usr/bin/env bash
# Plan rule 11 and the port's lag rules (AGENTS.md "Timers, ticks and :has() in the app"), on the page's source.
# Plain bash and grep, so CI needs nothing installed. It first checks itself against the probe lines below, then
# the source. Prints one line per hit and exits 1 if there are any. Locked (scripts/locked-paths.txt), like its
# allowlist.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

allowed=$(grep -v -e '^#' -e '^$' app/scripts/guards-allowed.txt || true)
is_allowed() { grep -qxF -- "$1" <<<"$allowed"; }

# A name that isn't part of a longer one: start of line or a character no identifier has before it.
b='(^|[^[:alnum:]_$])'
# What only the allowlisted files may do. Timers and frame waits, raw or through the counted helpers (also when
# they're imported, renamed or reached through a module); sockets; fetches; window and document listeners.
timers="setInterval|setTimeout|requestAnimationFrame|${b}(delay|frame|debounce)[[:space:]]*([(,}]|as[[:space:]]|$)"
sockets="${b}WebSocket\("
fetches="${b}fetch\("
listeners="(window|document|globalThis|self)\.addEventListener\("
# What applies everywhere, in its own scope.
tauri="@tauri-apps/|${b}listen\("
waits="${b}(tick|frame)\(|requestAnimationFrame"
body="observe\(document\.body"
markdown="from ['\"](marked|markdown-it|micromark|remark[^'\"]*|mdast[^'\"]*|unified)['\"]"
has=":has\("

# match FILE PATTERN WHY: every line of FILE that matches, skipping comment lines, as "LINE: text  (WHY)".
match() {
  { grep -nE "$2" "$1" || true; } | { grep -vE '^[0-9]+:[[:space:]]*(//|\*|/\*)' || true; } | sed "s|\$|  ($3)|"
}

# Per-row components: anything under lib/chat/, and any <Name>Row.svelte.
is_row() { [[ $1 == app/src/lib/chat/* || $1 == *Row.svelte ]]; }

# hits PATH [FILE]: every rule PATH is under, checked on FILE (PATH itself unless given), as "PATH:LINE: ...".
hits() {
  local path=$1 file=${2:-$1}
  {
    if ! is_allowed "$path"; then
      match "$file" "$timers" "no timers or frame waits, raw or through the counted helpers, outside the allowlist"
      match "$file" "$sockets" "sockets only through TrackedWebSocket"
      match "$file" "$fetches" "fetches only in the allowlisted files (api.ts)"
      match "$file" "$listeners" "window and document listeners only in the counted helpers (<svelte:window> is fine)"
    fi
    [[ $path == app/src/lib/host/* ]] || match "$file" "$tauri" "Tauri only in app/src/lib/host/"
    ! is_row "$path" || match "$file" "$waits" "no tick or frame waits in per-row components"
    match "$file" "$body" "no watcher on document.body"
    [[ $path == app/src/lib/editor/preview/* ]] || match "$file" "$markdown" "markdown comes as blocks from the backend; only the editor preview parses it"
    match "$file" "$has" "no CSS :has()"
  } | sed "s|^|$path:|"
}

# Self-test: each probe line must be caught, or not, as it says, so the rules and their scopes can't drift.
probe=$(mktemp)
trap 'rm -f "$probe"' EXIT
selftest=0
while IFS='|' read -r expect path code; do
  printf '%s\n' "$code" > "$probe"
  if [ -n "$(hits "$path" "$probe")" ]; then got=catch; else got=pass; fi
  if [ "$got" != "$expect" ]; then
    echo "guards self-test: expected $expect, got $got: $path: $code"
    selftest=1
  fi
done <<'PROBE'
catch|app/src/lib/x/A.svelte|import { listen } from '@tauri-apps/api/event';
pass|app/src/lib/host/opener.ts|import { openUrl } from '@tauri-apps/plugin-opener';
catch|app/src/lib/host/opener.ts|setTimeout(() => {}, 1);
catch|app/src/lib/x/a.ts|setInterval(() => {}, 1);
catch|app/src/lib/x/a.ts|window.setTimeout(run, 1);
catch|app/src/lib/x/a.ts|globalThis.requestAnimationFrame(run);
catch|app/src/lib/x/a.ts|import { delay as wait } from '../debug.svelte';
catch|app/src/lib/x/a.ts|import { counters, frame } from '../debug.svelte';
catch|app/src/lib/x/a.ts|  debounce,
catch|app/src/lib/x/a.ts|void delay(5, signal);
catch|app/src/lib/x/a.ts|void debug.frame(signal);
catch|app/src/lib/x/a.ts|const save = debounce(run, 300, signal);
catch|app/src/lib/x/a.ts|const ws = new WebSocket('ws://x');
catch|app/src/lib/x/a.ts|const ws = new window.WebSocket('ws://x');
pass|app/src/lib/x/a.ts|const ws = new TrackedWebSocket('ws://x');
catch|app/src/lib/x/a.ts|void fetch('/x');
catch|app/src/lib/x/a.ts|void window.fetch('/x');
catch|app/src/lib/x/a.ts|void globalThis.fetch('/x');
catch|app/src/lib/x/a.ts|window.addEventListener('resize', run);
catch|app/src/lib/x/a.ts|document.addEventListener('keydown', run);
catch|app/src/lib/x/a.ts|globalThis.addEventListener('focus', run);
pass|app/src/lib/x/A.svelte|<svelte:window onresize={run} />
catch|app/src/lib/chat/Message.svelte|void tick();
catch|app/src/lib/sessions/SessionRow.svelte|await tick();
pass|app/src/lib/sessions/SessionList.svelte|await tick();
catch|app/src/lib/x/a.ts|new MutationObserver(run).observe(document.body);
catch|app/src/lib/x/A.svelte|import { marked } from 'marked';
pass|app/src/lib/editor/preview/Preview.svelte|import { marked } from 'marked';
catch|app/src/lib/x/A.svelte|  .a:has(.b) { color: red; }
pass|app/src/lib/x/a.ts|// setTimeout in a comment
pass|app/src/lib/x/A.svelte|  @keyframes spin { to { opacity: 1; } }
PROBE
if [ "$selftest" -ne 0 ]; then
  echo "guards: the self-test failed (app/scripts/guards.sh)"
  exit 1
fi

failed=0
for file in $(git ls-files 'app/src/*.ts' 'app/src/*.svelte' 'app/src/*.css'); do
  # A file git still lists but that's gone from the checkout (deleted, not yet staged) has nothing to check.
  [ -f "$file" ] || continue
  found=$(hits "$file")
  [ -z "$found" ] || { echo "$found"; failed=1; }
done

# The list only shrinks: a listed file that no longer does anything it's exempt from must come off it.
for file in $allowed; do
  if [ ! -f "$file" ]; then
    echo "$file is on app/scripts/guards-allowed.txt but doesn't exist: take it off"
    failed=1
  elif [ -z "$(match "$file" "$timers|$sockets|$fetches|$listeners" .)" ]; then
    echo "$file is on app/scripts/guards-allowed.txt but needs nothing it allows: take it off"
    failed=1
  fi
done

if [ "$failed" -ne 0 ]; then
  echo "guards: failed (app/scripts/guards.sh)"
  exit 1
fi
echo "guards: ok"
