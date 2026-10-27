#!/usr/bin/env bash
# dotnet/scripts/agents-file.sh ADAPTERS OUT
# Installs the pinned ACP adapters under ADAPTERS (with pnpm) if they're missing, and writes OUT: the agents file
# Assembly.Desktop and Assembly.Remote serve read (AgentDescriptor). Codex runs with this machine's own ~/.codex and login;
# CODEX_CONFIG sets the model for Assembly's sessions only (its config file stays as it is) until the model menu.
# Which MCP servers an agent gets is provisional until Jesse names the basic set. For now: ASSEMBLY_USER_MCP=off (the
# default) turns off, for Assembly's Codex sessions only, every MCP server named in ~/.codex/config.toml (read, never
# written), while Codex's plugins load as it loads them; ASSEMBLY_USER_MCP=on loads the servers too. Claude Code's
# claude.ai connectors (ENABLE_CLAUDEAI_MCP_SERVERS) are off, at about 4,400 prompt tokens a session;
# ASSEMBLY_CLAUDEAI_MCP=on turns them back on. Codex opts into the AIR session index so the
# backend can title each session, which stops codex-acp starting a second thread, with its own servers, to make one.
# Both agents keep their todo list off unless asked: Claude Code's todo tools need CLAUDE_CODE_ENABLE_TODO_TOOLS, and
# Codex's update_plan needs tools.update_plan.enabled. Each sends that list as ACP plan updates, which become plan rows.
set -euo pipefail
adapters=$1 out=$2
claude_version=0.94.0 codex_version=2.2.2
version() { node -p "require('$adapters/node_modules/@agentclientprotocol/$1/package.json').version" 2>/dev/null || true; }
mkdir -p "$adapters"
[ -f "$adapters/package.json" ] || echo '{"private":true}' > "$adapters/package.json"
want=""
[ "$(version claude-agent-acp)" = "$claude_version" ] || want="$want @agentclientprotocol/claude-agent-acp@$claude_version"
[ "$(version codex-acp)" = "$codex_version" ] || want="$want @agentclientprotocol/codex-acp@$codex_version"
# pnpm 12 fails on skipped build scripts, as in scripts/deploy.sh.
# shellcheck disable=SC2086
[ -z "$want" ] || (cd "$adapters" && pnpm add --config.strict-dep-builds=false $want) >&2
ASSEMBLY_NODE=$(command -v node) ASSEMBLY_ADAPTERS=$adapters ASSEMBLY_USER_MCP=${ASSEMBLY_USER_MCP:-off} \
  ASSEMBLY_CLAUDEAI_MCP=${ASSEMBLY_CLAUDEAI_MCP:-off} python3 -I - > "$out" <<'PY'
import json, os, tomllib
codex = {"model": "gpt-5.6-luna", "tools": {"update_plan": {"enabled": True}}}
if os.environ["ASSEMBLY_USER_MCP"] == "off":
    path = os.path.expanduser("~/.codex/config.toml")
    config = tomllib.load(open(path, "rb")) if os.path.exists(path) else {}
    codex["mcp_servers"] = {name: {"enabled": False} for name in sorted(config.get("mcp_servers", {}))}
claude_env = {"CLAUDE_CODE_ENABLE_TODO_TOOLS": "1"}
if os.environ["ASSEMBLY_CLAUDEAI_MCP"] == "off":
    claude_env["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false"
node, adapters = os.environ["ASSEMBLY_NODE"], os.environ["ASSEMBLY_ADAPTERS"]
acp = lambda name: f"{adapters}/node_modules/@agentclientprotocol/{name}/dist/index.js"
# No AIR opt-in for Claude Code: any one makes claude-agent-acp treat us as JetBrains AIR, which moves its
# subagents off the standard child sessions. Its background work is read from what it sends any client.
print(json.dumps([
    {"id": "claude", "name": "Claude Code", "command": node, "args": [acp("claude-agent-acp")],
     "env": claude_env},
    {"id": "codex", "name": "Codex", "command": node, "args": [acp("codex-acp")],
     "env": {"CODEX_CONFIG": json.dumps(codex)}, "air": ["sessionIndex", "asyncTasks"]},
], indent=2))
PY
