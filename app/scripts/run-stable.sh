#!/bin/sh
# Runs the primary Assembly shell WITHOUT the file watcher.
#
# `tauri dev` rebuilds and relaunches the window every time anything under
# src-tauri changes, which makes the app unusable for testing while other work
# is in flight. This starts the same debug binary directly, against a plain
# vite server, so nothing restarts until you ask it to. Rebuild by hand with
# `cargo build --manifest-path src-tauri/Cargo.toml` and run this again.
#
# Adapter wrappers come from the downloaded payloads under src-tauri/adapters.
set -eu

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
APP_HOME="$SCRIPT_DIR/../src-tauri/adapters"
APP_DIR="$SCRIPT_DIR/.."
BIN="$APP_DIR/src-tauri/target/debug/mac-command-bar-webview-preview"

CODEX_WRAPPER="$APP_HOME/codex-acp"
CLAUDE_WRAPPER="$APP_HOME/claude-agent-acp"
AGY_WRAPPER="$APP_HOME/agy-acp"

if [ ! -x "$BIN" ]; then
  echo "run-stable: no debug binary. Build it first:" >&2
  echo "  cargo build --manifest-path src-tauri/Cargo.toml" >&2
  exit 1
fi
if [ ! -x "$CODEX_WRAPPER" ] || [ ! -x "$CLAUDE_WRAPPER" ]; then
  echo "run-stable: adapter wrappers not found under $APP_HOME." >&2
  echo "run-stable: run 'pnpm prepare:release-adapters' first." >&2
  exit 1
fi

sha() { shasum -a 256 "$1" | awk '{print $1}'; }

MCB_CODEX_ACP_PATH="$CODEX_WRAPPER"
MCB_CODEX_ACP_SHA256="$(sha "$CODEX_WRAPPER")"
MCB_CLAUDE_AGENT_ACP_PATH="$CLAUDE_WRAPPER"
MCB_CLAUDE_AGENT_ACP_SHA256="$(sha "$CLAUDE_WRAPPER")"
export MCB_CODEX_ACP_PATH MCB_CODEX_ACP_SHA256
export MCB_CLAUDE_AGENT_ACP_PATH MCB_CLAUDE_AGENT_ACP_SHA256

if [ -x "$AGY_WRAPPER" ]; then
  MCB_AGY_ACP_PATH="$AGY_WRAPPER"
  MCB_AGY_ACP_SHA256="$(sha "$AGY_WRAPPER")"
  export MCB_AGY_ACP_PATH MCB_AGY_ACP_SHA256
fi

export MCB_ALLOW_KEYCHAIN_CREDENTIALS=1
unset CLAUDECODE CLAUDE_CODE_ENTRYPOINT CLAUDE_CODE_SSE_PORT CLAUDE_CODE_PLUGIN_ROOT CLAUDE_PLUGIN_DATA

# The checkout the dev server on 5177 is serving from, or nothing.
serving_dir() {
  port_pid="$(lsof -ti tcp:5177 2>/dev/null | head -1)" || return 0
  [ -n "${port_pid:-}" ] || return 0
  lsof -a -d cwd -p "$port_pid" -Fn 2>/dev/null | sed -n 's/^n//p' | head -1
}

APP_DIR_REAL="$(CDPATH= cd -- "$APP_DIR" && pwd -P)"
SERVING="$(serving_dir || true)"

# A dev server is found by port, and every checkout's vite answers on the same
# one. A server left running from another worktree therefore satisfies the
# "is one up?" check and quietly serves ITS frontend to THIS binary — which
# reads as "none of my changes did anything", with nothing on screen to say so.
# A whole morning went into that once. Refuse instead.
if [ -n "$SERVING" ]; then
  SERVING_REAL="$(CDPATH= cd -- "$SERVING" 2>/dev/null && pwd -P)" || SERVING_REAL="$SERVING"
  if [ "$SERVING_REAL" != "$APP_DIR_REAL" ]; then
    echo "run-stable: port 5177 is already served from a different checkout." >&2
    echo "  serving: $SERVING_REAL" >&2
    echo "  wanted:  $APP_DIR_REAL" >&2
    echo "run-stable: stop it first, then run this again:" >&2
    echo "  kill \$(lsof -ti tcp:5177)" >&2
    exit 1
  fi
fi

# The debug binary loads its pages from the dev server, so one has to be up.
# Started here rather than by the binary, and left running afterwards.
if ! curl -s -o /dev/null --max-time 2 http://127.0.0.1:5177/; then
  ( cd "$APP_DIR" && nohup pnpm exec vite --host 127.0.0.1 --port 5177 > /tmp/mcb-vite-stable.log 2>&1 & )
  until curl -s -o /dev/null --max-time 2 http://127.0.0.1:5177/; do sleep 1; done
fi

echo "run-stable: no watcher. Rebuild by hand and run this again to pick up changes."
exec "$BIN" "$@"
