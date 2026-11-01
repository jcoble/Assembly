#!/usr/bin/env bash
# dotnet/scripts/agents-file.sh ADAPTERS OUT
# Installs the latest published platform ACP adapter bundle under ADAPTERS and writes OUT: the agents file
# Assembly.Desktop and Assembly.Remote serve read (AgentDescriptor). Codex runs with this machine's own ~/.codex and login;
# CODEX_CONFIG sets the model for Assembly's sessions only (its config file stays as it is) until the model menu.
# Which MCP servers an agent gets is provisional until Jesse names the basic set. For now: ASSEMBLY_USER_MCP=off (the
# default) turns off, for Assembly's Codex sessions only, every MCP server named in ~/.codex/config.toml (read, never
# written), while Codex's plugins load as it loads them; ASSEMBLY_USER_MCP=on loads the servers too. Claude Code's
# claude.ai connectors (ENABLE_CLAUDEAI_MCP_SERVERS) are off, at about 4,400 prompt tokens a session;
# ASSEMBLY_CLAUDEAI_MCP=on turns them back on. The Claude launcher and both adapters are installed from the daily
# platform bundle that passed the workflow smoke check.
# Both agents keep their todo list off unless asked: Claude Code's todo tools need CLAUDE_CODE_ENABLE_TODO_TOOLS, and
# Codex's update_plan needs tools.update_plan.enabled. Each sends that list as ACP plan updates, which become plan rows.
set -euo pipefail
adapters=$1 out=$2
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) target=darwin-arm64 ;;
  Darwin-x86_64) target=darwin-x64 ;;
  Linux-x86_64) target=linux-x64 ;;
  *) echo "Unsupported adapter platform: $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac
asset="assembly-adapters-$target.tar.gz"
mkdir -p "$adapters"
download=$(mktemp -d)
trap 'rm -rf "$download"' EXIT
gh release download --repo jcoble/Assembly --pattern "$asset" --dir "$download"
tar -xzf "$download/$asset" -C "$adapters"
ASSEMBLY_NODE=$(command -v node) ASSEMBLY_CLAUDE=$(command -v claude || true) ASSEMBLY_CODEX=$(command -v codex || true) \
  ASSEMBLY_ADAPTERS=$adapters ASSEMBLY_USER_MCP=${ASSEMBLY_USER_MCP:-off} \
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
agents = [
    {"id": "claude", "name": "Claude Code", "command": node, "args": [f"{adapters}/claude-launcher.ts"],
     "env": claude_env},
    {"id": "codex", "name": "Codex", "command": node, "args": [acp("codex-acp")],
     "env": {"CODEX_CONFIG": json.dumps(codex)}},
]
# Each CLI's full path, for the utility model: a backend the Dock starts has only the system PATH.
for agent in agents:
    if cli := os.environ[f"ASSEMBLY_{agent['id'].upper()}"]:
        agent["cli"] = cli
print(json.dumps(agents, indent=2))
PY
