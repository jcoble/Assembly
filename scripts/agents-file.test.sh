#!/usr/bin/env bash
# Tests for dotnet/scripts/agents-file.sh: by default Claude Code's claude.ai connectors are off and Codex loads its
# plugins but none of the user's MCP servers; ASSEMBLY_CLAUDEAI_MCP=on turns the connectors back on and
# ASSEMBLY_USER_MCP=on loads the MCP servers too. Each agent names its CLI's full path when it's on PATH. Runs the script with a throwaway HOME, a made-up ~/.codex/config.toml
# and a fake latest-release bundle, and checks the agents file it writes. Run: bash scripts/agents-file.test.sh
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/home/.codex"
printf '[mcp_servers.alpha]\ncommand = "alpha"\n\n[plugins."beta@market"]\nenabled = true\n' >"$tmp/home/.codex/config.toml"
mkdir -p "$tmp/bundle/node_modules/@agentclientprotocol/claude-agent-acp/dist" \
  "$tmp/bundle/node_modules/@agentclientprotocol/codex-acp/dist" "$tmp/bin"
echo '{"private":true}' >"$tmp/bundle/package.json"
echo '{"lockfileVersion":3}' >"$tmp/bundle/package-lock.json"
echo '// test launcher' >"$tmp/bundle/claude-launcher.ts"
for name in claude-agent-acp codex-acp; do
  echo '{"version":"daily"}' >"$tmp/bundle/node_modules/@agentclientprotocol/$name/package.json"
  echo '// test adapter' >"$tmp/bundle/node_modules/@agentclientprotocol/$name/dist/index.js"
done
tar -czf "$tmp/daily.tar.gz" -C "$tmp/bundle" package.json package-lock.json claude-launcher.ts node_modules
cat >"$tmp/bin/gh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1 $2" == 'release download' ]]
shift 2
while (($#)); do
  case "$1" in
    --dir) dir=$2; shift 2 ;;
    --pattern) pattern=$2; shift 2 ;;
    --repo) shift 2 ;;
    *) exit 2 ;;
  esac
done
cp "$FAKE_DAILY_BUNDLE" "$dir/$pattern"
SH
chmod +x "$tmp/bin/gh"
# A PATH with node and a stand-in claude, and no codex.
ln -s "$(command -v node)" "$tmp/bin/node"
printf '#!/bin/sh\n' >"$tmp/bin/claude"
chmod +x "$tmp/bin/claude"
failed=0
# expect <name> <python assertion over claude_env and codex> [env assignments]: writes the agents file under those
# settings and checks it.
expect() {
  local name=$1 check=$2
  shift 2
  if ! env HOME="$tmp/home" PATH="$tmp/bin:/usr/bin:/bin" FAKE_DAILY_BUNDLE="$tmp/daily.tar.gz" "$@" bash "$here/../dotnet/scripts/agents-file.sh" "$tmp/adapters" "$tmp/agents.json" 2>"$tmp/err"; then
    echo "FAIL $name: the script failed: $(cat "$tmp/err")"; failed=1; return
  fi
  if python3 -I -c "
import json, sys
agents = {agent['id']: agent for agent in json.load(open(sys.argv[1]))}
claude_env = agents['claude']['env']
codex = json.loads(agents['codex']['env']['CODEX_CONFIG'])
assert $check, (claude_env, codex)
" "$tmp/agents.json" 2>"$tmp/err"; then echo "ok   $name"
  else echo "FAIL $name: $(tail -1 "$tmp/err")"; failed=1; fi
}
expect "by default the claude.ai connectors are off" "claude_env.get('ENABLE_CLAUDEAI_MCP_SERVERS') == 'false'"
expect "by default Codex's plugins load" "'plugins' not in codex"
expect "by default the user's MCP servers are off" "codex.get('mcp_servers') == {'alpha': {'enabled': False}}"
expect "ASSEMBLY_CLAUDEAI_MCP=on turns the connectors back on" "'ENABLE_CLAUDEAI_MCP_SERVERS' not in claude_env" ASSEMBLY_CLAUDEAI_MCP=on
expect "ASSEMBLY_USER_MCP=on loads the user's MCP servers" "'mcp_servers' not in codex and 'plugins' not in codex" ASSEMBLY_USER_MCP=on
expect "a CLI on PATH is named by its full path, and one that isn't is left out" \
  "agents['claude'].get('cli') == '$tmp/bin/claude' and 'cli' not in agents['codex']" PATH="$tmp/bin:/usr/bin:/bin"
expect "Claude uses the packaged launcher and Codex keeps its entry without AIR" \
  "agents['claude']['args'] == ['$tmp/adapters/claude-launcher.ts'] and agents['codex']['args'] == ['$tmp/adapters/node_modules/@agentclientprotocol/codex-acp/dist/index.js'] and 'air' not in agents['codex']"
exit $failed
