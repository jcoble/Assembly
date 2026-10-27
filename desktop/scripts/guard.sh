#!/usr/bin/env bash
# The window's rules (G0 in the GPUI switch plan; desktop/README.md), on desktop/'s Rust source. Plain bash and grep,
# so CI needs nothing installed and BSD grep on the Mac reads it the same. No allow lists: a rule holds for every file
# or it's changed with Jesse's OK. It first checks itself against the probe lines below, then the source. Prints one
# line per hit and exits 1 if there are any. scripts/lint.d/60-desktop-guard.sh runs it before every commit and in CI.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

# A name that isn't part of a longer one: no identifier character before it (b) or after it (e).
b='(^|[^[:alnum:]_])'
e='([^[:alnum:]_]|$)'
# Waits on time, timers and frame waits: only the clock, desktop/src/core/clock/, may.
timers="${b}Timer${e}|(\.|::)timer[[:space:]]*\(|${b}(request_animation_frame|on_next_frame|recv_timeout|park_timeout|with_timeout|block_with_timeout)${e}"
# Sockets and HTTP: only the socket client, desktop/src/core/socket/, may.
sockets="${b}(tungstenite|TcpStream|TcpListener|UdpSocket|ureq|reqwest|http_client|HttpClient)${e}|${b}hyper::"
# Sleeping, anywhere.
sleeps="${b}sleep${e}"
# A file that draws (implements Render or RenderOnce, or has a render function), and file reads in one.
draws="${b}impl([^[:alnum:]_]|[^[:alnum:]_].*[^[:alnum:]_])(Render|RenderOnce)[[:space:]]+for${e}|${b}fn[[:space:]]+render${e}"
files="${b}fs${e}|${b}File::"

# match FILE PATTERN WHY: every line of FILE that matches, skipping comment lines, as "LINE: text  (WHY)".
match() {
  { grep -nE "$2" "$1" || true; } | { grep -vE '^[0-9]+:[[:space:]]*//' || true; } | sed "s|\$|  ($3)|"
}

# hits PATH [FILE]: every rule PATH is under, checked on FILE (PATH itself unless given), as "PATH:LINE: ...".
hits() {
  local path=$1 file=${2:-$1}
  {
    [[ $path == desktop/src/core/clock/* ]] || match "$file" "$timers" "timers and frame waits only in core/clock/"
    [[ $path == desktop/src/core/socket/* ]] || match "$file" "$sockets" "sockets and HTTP only in core/socket/"
    match "$file" "$sleeps" "no sleeping"
    [ -z "$(match "$file" "$draws" .)" ] || match "$file" "$files" "no file reads in a file that draws"
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
    echo "guard self-test: expected $expect, got $got: $path: $code"
    selftest=1
  fi
done <<'PROBE'
catch|desktop/src/features/x/a.rs|cx.background_executor().timer(Duration::from_secs(1)).await;
catch|desktop/src/features/x/a.rs|use gpui::Timer;
catch|desktop/src/features/x/a.rs|BackgroundExecutor::timer(&executor, d).await;
catch|desktop/src/features/x/a.rs|Timer::after(Duration::from_millis(5)).await;
catch|desktop/src/features/x/a.rs|window.on_next_frame(move |_, _| {});
catch|desktop/src/features/x/a.rs|window.request_animation_frame();
catch|desktop/src/core/socket/a.rs|let msg = rx.recv_timeout(Duration::from_secs(1));
catch|desktop/src/features/x/a.rs|let r = fut.with_timeout(Duration::from_secs(1), cx.background_executor()).await;
catch|desktop/src/features/x/a.rs|cx.background_executor().block_with_timeout(d, fut);
pass|desktop/src/core/clock/a.rs|Timer::after(Duration::from_millis(5)).await;
pass|desktop/src/features/x/a.rs|struct TimerView;
pass|desktop/src/features/x/a.rs|let timer_label = clock.read(cx).label();
pass|desktop/src/features/x/a.rs|// Timer::after is only for the clock.
catch|desktop/src/features/x/a.rs|use tungstenite::connect;
catch|desktop/src/core/x/a.rs|let stream = std::net::TcpStream::connect(addr)?;
catch|desktop/src/features/x/a.rs|let body = ureq::get(url).call()?;
catch|desktop/src/features/x/a.rs|let client = cx.http_client();
catch|desktop/src/features/x/a.rs|use hyper::Client;
pass|desktop/src/core/socket/a.rs|let (socket, _) = tungstenite::client(request, stream)?;
pass|desktop/src/core/socket/a.rs|let stream = std::net::TcpStream::connect(addr)?;
pass|desktop/src/features/x/a.rs|let link = hyperlink(text);
catch|desktop/src/core/clock/a.rs|std::thread::sleep(Duration::from_secs(1));
catch|desktop/src/core/socket/a.rs|use std::thread::sleep;
catch|desktop/src/features/x/a.rs|sleep(RECONNECT_AFTER);
catch|desktop/src/features/x/a.rs|use std::thread::{Builder, sleep as wait};
pass|desktop/src/features/x/a.rs|host.prevent_sleep(true);
catch|desktop/src/features/x/a.rs|impl Render for A {} fn f() { std::fs::read("x"); }
catch|desktop/src/features/x/a.rs|impl<T> gpui::RenderOnce for A<T> {} fn f() { let f = File::open("x"); }
catch|desktop/src/features/x/a.rs|fn render(&mut self) { let s = fs::read_to_string("x"); }
catch|desktop/src/features/x/a.rs|use std::{env, fs as disk}; fn render(&mut self) {}
pass|desktop/src/features/x/a.rs|fn load() { std::fs::read("x"); }
pass|desktop/src/features/x/a.rs|impl Render for A {} fn f() { let p = prefs::theme(); }
pass|desktop/src/features/x/a.rs|impl Render for A {} fn f() { cx.dispatch(OpenFile::new(path)); }
PROBE
if [ "$selftest" -ne 0 ]; then
  echo "guard: the self-test failed (desktop/scripts/guard.sh)"
  exit 1
fi

failed=0
for file in $(git ls-files 'desktop/*.rs'); do
  # A file git still lists but that's gone from the checkout (deleted, not yet staged) has nothing to check.
  [ -f "$file" ] || continue
  found=$(hits "$file")
  [ -z "$found" ] || { echo "$found"; failed=1; }
done

if [ "$failed" -ne 0 ]; then
  echo "guard: failed (desktop/scripts/guard.sh)"
  exit 1
fi
echo "guard: ok"
