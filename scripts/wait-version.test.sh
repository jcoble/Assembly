#!/usr/bin/env bash
# Tests for scripts/wait-version.sh against a fake backend: one that starts late passes, one that answers with another
# commit first (an old process not yet stopped) passes once the right one answers, and one that never answers, or only
# with another commit, or only after the deadline, fails at the deadline with one line naming its port and what it
# last saw.
# Run: bash scripts/wait-version.test.sh
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
dir=$(mktemp -d)
pids=()
trap 'kill ${pids[@]+"${pids[@]}"} 2>/dev/null; rm -rf "$dir"' EXIT
failed=0

# fake <name> <listen after s> <old commit> <old for s> <commit> [answer after s]: a backend on a free loopback port,
# which refuses connections until it listens, then answers /version with the old commit, then with the commit, each
# answer taking the given time. Sets $port.
fake() {
  python3 - "$dir/$1.port" "$2" "$3" "$4" "$5" "${6:-0}" <<'PY' &
import http.server, json, socket, sys, time
port_file, listen_after, old, old_for, commit, slow = sys.argv[1], float(sys.argv[2]), sys.argv[3], float(sys.argv[4]), sys.argv[5], float(sys.argv[6])
sock = socket.socket()
sock.bind(("127.0.0.1", 0))
open(port_file, "w").write(str(sock.getsockname()[1]))
time.sleep(listen_after)
sock.listen()
started = time.monotonic()
class Version(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        time.sleep(slow)
        body = json.dumps({"commit": old if time.monotonic() - started < old_for else commit}).encode()
        self.send_response(200); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *args): pass
server = http.server.HTTPServer(("127.0.0.1", 0), Version, bind_and_activate=False)
server.socket.close(); server.socket = sock
server.serve_forever()
PY
  pids+=($!)
  for _ in $(seq 50); do [ -s "$dir/$1.port" ] && break; sleep 0.1; done
  port=$(cat "$dir/$1.port")
}

# expect <name> <exit code> <output pattern> <seconds>: runs wait-version on the fake's port, stopped by `timeout` if
# it runs well past its deadline.
expect() {
  local out got took start=$SECONDS
  out=$(echo token | timeout $(($4 + 10)) "$here/wait-version.sh" "the fake" "http://127.0.0.1:$port" new "$4" 2>&1)
  got=$?
  took=$((SECONDS - start))
  # A failure comes at the deadline, not before it, and nothing runs more than a second past it.
  if [ "$got" = "$2" ] && [[ "$out" =~ $3 ]] && [ "$(wc -l <<<"$out")" = 1 ] && { [ "$2" = 0 ] || [ "$took" -ge "$4" ]; } \
    && [ "$took" -le $(($4 + 1)) ]; then echo "ok   $1 ($took s)"
  else echo "FAIL $1: exit $got, wanted $2, after $took s; output '$out'"; failed=1; fi
}

fake late 2 old 0 new
expect "a backend that starts late passes once it answers" 0 '^new$' 10
fake old-first 0 old 2 new
expect "an old process answering first doesn't count; the new one does" 0 '^new$' 10
fake never 999 old 0 new
expect "a backend that never answers fails at the deadline, naming its port" 1 "port $port.*in 3 s.*no answer" 3
fake stale 0 old 999 new
expect "one that only answers with another commit fails, naming what it saw" 1 "port $port.*in 3 s.*old" 3
fake slow 0 old 0 new 4
expect "one that answers only after the deadline fails at the deadline" 1 "port $port.*in 2 s" 2
exit $failed
