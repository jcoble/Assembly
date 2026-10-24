#!/bin/sh
# Development uses the same downloaded adapter payloads as release packaging.
# A verified installed provider update takes priority in ProviderRegistry.
set -eu
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
ADAPTER_DIR="$SCRIPT_DIR/../src-tauri/adapters"
if [ ! -f "$ADAPTER_DIR/manifest.json" ] || [ ! -x "$ADAPTER_DIR/agy_acp_server.par" ]; then
  pnpm prepare:release-adapters
fi
sha() { shasum -a 256 "$1" | awk '{print $1}'; }
MCB_CODEX_ACP_PATH="$ADAPTER_DIR/codex-acp"
MCB_CLAUDE_AGENT_ACP_PATH="$ADAPTER_DIR/claude-agent-acp"
MCB_AGY_ACP_PATH="$ADAPTER_DIR/agy-acp"
MCB_CODEX_ACP_SHA256="$(sha "$MCB_CODEX_ACP_PATH")"
MCB_CLAUDE_AGENT_ACP_SHA256="$(sha "$MCB_CLAUDE_AGENT_ACP_PATH")"
MCB_AGY_ACP_SHA256="$(sha "$MCB_AGY_ACP_PATH")"
export MCB_CODEX_ACP_PATH MCB_CODEX_ACP_SHA256 MCB_CLAUDE_AGENT_ACP_PATH MCB_CLAUDE_AGENT_ACP_SHA256 MCB_AGY_ACP_PATH MCB_AGY_ACP_SHA256
unset CODEX_CONFIG CLAUDECODE CLAUDE_CODE_ENTRYPOINT CLAUDE_CODE_SSE_PORT CLAUDE_CODE_PLUGIN_ROOT CLAUDE_PLUGIN_DATA
# Read the Claude login already on this Mac instead of asking for credentials the
# user has entered once. The quota client keeps this behind a flag; nothing set
# it, so the keychain path was dead and the panel fell back to manual entry.
export MCB_ALLOW_KEYCHAIN_CREDENTIALS=1

# A previous shell can exit while its Vite server remains alive. In that case,
# attach the new shell to this checkout's server instead of failing on port 5177.
if [ "$#" -eq 0 ]; then
  VITE_PID="$(lsof -tiTCP:5177 -sTCP:LISTEN 2>/dev/null | head -n 1)"
  if [ -n "$VITE_PID" ]; then
    VITE_CWD="$(lsof -a -p "$VITE_PID" -d cwd -Fn 2>/dev/null | sed -n 's/^n//p' | head -n 1)"
    APP_DIR="$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)"
    if [ "$VITE_CWD" = "$APP_DIR" ]; then
      echo "dev-app: attaching to the existing Vite server on 127.0.0.1:5177"
      set -- --config "$APP_DIR/src-tauri/tauri.dev.attach.conf.json"
    fi
  fi
fi

exec pnpm exec tauri dev "$@"
