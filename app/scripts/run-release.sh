#!/bin/sh
# Launches the RELEASE build of the primary Assembly shell with the same trusted ACP
# adapters the dev script registers. Without these env vars the provider
# registry is empty and every send fails with "No trusted adapter is
# registered for this provider". Reuses the wrapper scripts dev-app.sh
# prepared under src-tauri/adapters.
set -eu

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
APP_HOME="$SCRIPT_DIR/../src-tauri/adapters"
CODEX_WRAPPER="$APP_HOME/codex-acp"
CLAUDE_WRAPPER="$APP_HOME/claude-agent-acp"
AGY_WRAPPER="$APP_HOME/agy-acp"

if [ ! -x "$CODEX_WRAPPER" ] || [ ! -x "$CLAUDE_WRAPPER" ]; then
  echo "run-release: adapter wrappers not found under $APP_HOME." >&2
  echo "run-release: run 'pnpm prepare:release-adapters' first." >&2
  exit 1
fi

sha() { shasum -a 256 "$1" | awk '{print $1}'; }

MCB_CODEX_ACP_PATH="$CODEX_WRAPPER"
MCB_CODEX_ACP_SHA256="$(sha "$CODEX_WRAPPER")"
MCB_CLAUDE_AGENT_ACP_PATH="$CLAUDE_WRAPPER"
MCB_CLAUDE_AGENT_ACP_SHA256="$(sha "$CLAUDE_WRAPPER")"
export MCB_CODEX_ACP_PATH MCB_CODEX_ACP_SHA256 MCB_CLAUDE_AGENT_ACP_PATH MCB_CLAUDE_AGENT_ACP_SHA256

if [ -x "$AGY_WRAPPER" ]; then
  MCB_AGY_ACP_PATH="$AGY_WRAPPER"
  MCB_AGY_ACP_SHA256="$(sha "$AGY_WRAPPER")"
  export MCB_AGY_ACP_PATH MCB_AGY_ACP_SHA256
  echo "run-release: antigravity adapter $MCB_AGY_ACP_PATH"
else
  unset MCB_AGY_ACP_PATH MCB_AGY_ACP_SHA256
  echo "run-release: Antigravity adapter unavailable; skipping (prepare the official adapter payload)."
fi

# Read the Claude login already on this Mac instead of asking for credentials the
# user has entered once. The quota client keeps this behind a flag; nothing set
# it, so the keychain path was dead and the panel fell back to manual entry.
export MCB_ALLOW_KEYCHAIN_CREDENTIALS=1
unset CLAUDECODE CLAUDE_CODE_ENTRYPOINT CLAUDE_CODE_SSE_PORT CLAUDE_CODE_PLUGIN_ROOT CLAUDE_PLUGIN_DATA

exec "$SCRIPT_DIR/../src-tauri/target/release/mac-command-bar-webview-preview" "$@"
