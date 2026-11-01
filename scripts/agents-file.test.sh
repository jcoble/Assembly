#!/usr/bin/env bash
# Tests agents-file's release selection, offline cache, atomic replacement, and generated agent descriptors.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/home/.codex" "$tmp/bin" "$tmp/releases"
printf '[mcp_servers.alpha]\ncommand = "alpha"\n\n[plugins."beta@market"]\nenabled = true\n' >"$tmp/home/.codex/config.toml"
ln -s "$(command -v node)" "$tmp/bin/node"
printf '#!/bin/sh\n' >"$tmp/bin/claude"
chmod +x "$tmp/bin/claude"

make_asset() {
  local tag=$1 claude_version=$2 codex_version=$3 marker=$4 obsolete=${5:-no} truncate=${6:-no}
  local folder="$tmp/releases/$tag" bundle="$tmp/bundle-$tag" target=linux-x64
  mkdir -p "$folder" "$bundle/node_modules/@agentclientprotocol/claude-agent-acp/dist" \
    "$bundle/node_modules/@agentclientprotocol/codex-acp/dist"
  echo '{"private":true}' >"$bundle/package.json"
  echo '{"lockfileVersion":3}' >"$bundle/package-lock.json"
  echo "export const launcher = '$marker';" >"$bundle/claude-launcher.ts"
  [[ "$obsolete" != yes ]] || echo old-only >"$bundle/obsolete-upstream-file.txt"
  for pair in "claude-agent-acp $claude_version" "codex-acp $codex_version"; do
    read -r name version <<<"$pair"
    echo "{\"version\":\"$version\"}" >"$bundle/node_modules/@agentclientprotocol/$name/package.json"
    echo "// $marker $name" >"$bundle/node_modules/@agentclientprotocol/$name/dist/index.js"
  done
  local members=(package.json package-lock.json claude-launcher.ts node_modules)
  [[ "$obsolete" != yes ]] || members+=(obsolete-upstream-file.txt)
  tar -czf "$folder/assembly-adapters-$target.tar.gz" -C "$bundle" "${members[@]}"
  if [[ "$truncate" == yes ]]; then
    head -c 64 "$folder/assembly-adapters-$target.tar.gz" >"$folder/truncated.tar.gz"
    mv "$folder/truncated.tar.gz" "$folder/assembly-adapters-$target.tar.gz"
  fi
  local checksum
  checksum=$(sha256sum "$folder/assembly-adapters-$target.tar.gz" | cut -d' ' -f1)
  python3 - "$folder/assembly-adapters-$target.json" "$target" "$claude_version" "$codex_version" "$checksum" <<'PY'
import json, sys
path, target, claude, codex, checksum = sys.argv[1:]
versions = {"claude-agent-acp": claude, "codex-acp": codex}
manifest = {
    "schemaVersion": 1, "target": target, "versions": versions,
    "entrypoints": {"claude-agent-acp": "claude-launcher.ts", "codex-acp": "node_modules/@agentclientprotocol/codex-acp/dist/index.js"},
    "archive": {"file": "assembly-adapters-" + target + ".tar.gz", "sha256": checksum},
    "smoke": {name: {"protocolVersion": 2, "info": {"version": version}, "canSteer": True} for name, version in versions.items()},
}
json.dump(manifest, open(path, "w"))
PY
}

make_asset assembly-providers-201-1 0.99.0 2.4.0 prior yes
make_asset assembly-providers-202-1 0.99.1 2.5.0 selected yes
make_asset assembly-providers-203-1 0.99.2 2.5.1 incomplete no
make_asset assembly-providers-206-1 0.99.3 2.5.2 corrupt no yes
make_asset assembly-providers-207-1 0.99.4 2.5.3 malformed no
make_asset assembly-providers-208-1 0.99.5 2.5.4 replacement no
make_asset assembly-providers-209-1 0.99.6 2.5.5 download-failure no
cat >"$tmp/releases.json" <<'JSON'
[
 {"tagName":"assembly-app-2026.11","isLatest":true,"isDraft":false,"isPrerelease":false,"publishedAt":"2026-11-07T00:00:00Z","assets":[]},
 {"tagName":"assembly-providers-201-1","isLatest":false,"isDraft":false,"isPrerelease":false,"publishedAt":"2026-11-01T00:00:00Z","assets":["assembly-adapters-linux-x64.tar.gz","assembly-adapters-linux-x64.json"]},
 {"tagName":"assembly-providers-202-1","isLatest":false,"isDraft":false,"isPrerelease":false,"publishedAt":"2026-11-02T00:00:00Z","assets":["assembly-adapters-linux-x64.tar.gz","assembly-adapters-linux-x64.json"]},
 {"tagName":"assembly-providers-203-1","isLatest":false,"isDraft":false,"isPrerelease":false,"publishedAt":"2026-11-06T00:00:00Z","assets":["assembly-adapters-linux-x64.tar.gz"]},
 {"tagName":"assembly-providers-204-1","isLatest":false,"isDraft":true,"isPrerelease":false,"publishedAt":"2026-11-08T00:00:00Z","assets":["assembly-adapters-linux-x64.tar.gz","assembly-adapters-linux-x64.json"]},
 {"tagName":"assembly-providers-205-1","isLatest":false,"isDraft":false,"isPrerelease":true,"publishedAt":"2026-11-09T00:00:00Z","assets":["assembly-adapters-linux-x64.tar.gz","assembly-adapters-linux-x64.json"]}
]
JSON
cat >"$tmp/bin/gh" <<'JS'
#!/usr/bin/env node
const fs = require("node:fs"), path = require("node:path");
const [group, action, ...args] = process.argv.slice(2);
const releases = JSON.parse(fs.readFileSync(process.env.FAKE_RELEASES_FILE, "utf8"));
const calls = process.env.FAKE_GH_CALLS;
const log = value => calls && fs.appendFileSync(calls, JSON.stringify(value) + "\n");
if (group !== "release") throw Error("unexpected gh operation");
if (action === "list") {
  log({action, args});
  if (process.env.FAKE_FAIL_LIST) { console.error("fake release list unavailable"); process.exit(17); }
  console.log(JSON.stringify(releases.map(({tagName,isDraft,isPrerelease,publishedAt}) => ({tagName,isDraft,isPrerelease,publishedAt}))));
} else if (action === "view") {
  const tag = args[0], release = releases.find(item => item.tagName === tag);
  log({action, tag});
  console.log(JSON.stringify({assets: (release?.assets ?? []).map(name => ({name}))}));
} else if (action === "download") {
  const tag = args[0];
  log({action, tag});
  if (process.env.FAKE_FAIL_DOWNLOAD) { console.error("fake release download unavailable"); process.exit(17); }
  const dir = args[args.indexOf("--dir") + 1], patterns = args.flatMap((arg, i) => arg === "--pattern" ? [args[i + 1]] : []);
  for (const name of patterns) fs.copyFileSync(path.join(process.env.FAKE_ASSET_ROOT, tag, name), path.join(dir, name));
} else throw Error("unexpected gh release operation " + action);
JS
chmod +x "$tmp/bin/gh"

run_agents() {
  env HOME="$tmp/home" PATH="$tmp/bin:/usr/bin:/bin" FAKE_RELEASES_FILE="$tmp/releases.json" \
    FAKE_ASSET_ROOT="$tmp/releases" FAKE_GH_CALLS="$tmp/gh-calls.jsonl" "$@" \
    bash "$here/../dotnet/scripts/agents-file.sh" "$tmp/adapters" "$tmp/agents.json"
}
expect_config() {
  local name=$1 condition=$2
  shift 2
  if ! run_agents "$@" 2>"$tmp/err"; then echo "FAIL $name: $(cat "$tmp/err")"; return 1; fi
  python3 -I - "$tmp/agents.json" "$condition" <<'PY'
import json, sys
agents = {agent["id"]: agent for agent in json.load(open(sys.argv[1]))}
claude_env = agents["claude"]["env"]
codex = json.loads(agents["codex"]["env"]["CODEX_CONFIG"])
condition = sys.argv[2]
checks = {
 "claude_connectors_off": claude_env.get("ENABLE_CLAUDEAI_MCP_SERVERS") == "false",
 "codex_plugins_default": "plugins" not in codex,
 "user_mcp_off": codex.get("mcp_servers") == {"alpha": {"enabled": False}},
 "claude_connectors_on": "ENABLE_CLAUDEAI_MCP_SERVERS" not in claude_env,
 "user_mcp_on": "mcp_servers" not in codex and "plugins" not in codex,
 "cli_paths": agents["claude"].get("cli", "").endswith("/claude") and "cli" not in agents["codex"],
 "entries": agents["claude"]["args"][0].endswith("/claude-launcher.ts") and agents["codex"]["args"][0].endswith("/node_modules/@agentclientprotocol/codex-acp/dist/index.js") and "air" not in agents["codex"],
}
assert checks[condition], (condition, agents)
PY
  echo "ok   $name"
}

# The app release is designated latest, but only the newest complete provider-tagged release is eligible.
expect_config "selects newest complete provider release, not app latest or incomplete/draft releases" entries
python3 - "$tmp/gh-calls.jsonl" <<'PY'
import json, sys
calls = [json.loads(line) for line in open(sys.argv[1])]
downloads = [call["tag"] for call in calls if call["action"] == "download"]
assert downloads == ["assembly-providers-202-1"], downloads
assert any(call.get("tag") == "assembly-providers-203-1" for call in calls)
assert not any(call.get("tag") in ("assembly-providers-204-1", "assembly-providers-205-1") for call in calls)
PY
expect_config "by default claude.ai connectors are off" claude_connectors_off
expect_config "by default Codex plugins load" codex_plugins_default
expect_config "by default user MCP servers are off" user_mcp_off
expect_config "ASSEMBLY_CLAUDEAI_MCP=on turns connectors back on" claude_connectors_on ASSEMBLY_CLAUDEAI_MCP=on
expect_config "ASSEMBLY_USER_MCP=on loads user MCP servers" user_mcp_on ASSEMBLY_USER_MCP=on
expect_config "a CLI on PATH is named by full path" cli_paths PATH="$tmp/bin:/usr/bin:/bin"

active_bundle=$(python3 - "$tmp/agents.json" <<'PY'
import json, os, sys
agents = {a["id"]: a for a in json.load(open(sys.argv[1]))}
print(os.path.dirname(agents["claude"]["args"][0]))
PY
)
[[ "$(cat "$active_bundle/provider-tag")" == assembly-providers-202-1 ]]
before_descriptor=$(sha256sum "$tmp/agents.json" | cut -d' ' -f1)
before_tree=$(cd "$active_bundle" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -d' ' -f1)

# A provider lookup failure with a valid cache succeeds and leaves the descriptor and tree byte-for-byte unchanged.
if ! run_agents FAKE_FAIL_LIST=1 2>"$tmp/offline.err"; then echo "FAIL cached offline startup: $(cat "$tmp/offline.err")"; exit 1; fi
[[ "$(sha256sum "$tmp/agents.json" | cut -d' ' -f1)" == "$before_descriptor" ]]
[[ "$(cd "$active_bundle" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -d' ' -f1)" == "$before_tree" ]]
rg -q 'retaining validated cached bundle assembly-providers-202-1' "$tmp/offline.err"
echo "ok   cached bundle starts offline and update failure preserves saved state"

# A successful lookup followed by an asset download failure also keeps the validated cache.
python3 - "$tmp/releases.json" <<'PY'
import json, sys
p=sys.argv[1]; releases=json.load(open(p)); releases.append({"tagName":"assembly-providers-209-1","isDraft":False,"isPrerelease":False,"publishedAt":"2026-11-03T00:00:00Z","assets":["assembly-adapters-linux-x64.tar.gz","assembly-adapters-linux-x64.json"]}); json.dump(releases,open(p,"w"))
PY
if ! run_agents FAKE_FAIL_DOWNLOAD=1 2>"$tmp/download.err"; then echo "FAIL download fallback: $(cat "$tmp/download.err")"; exit 1; fi
[[ "$(sha256sum "$tmp/agents.json" | cut -d' ' -f1)" == "$before_descriptor" ]]
[[ "$(cd "$active_bundle" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -d' ' -f1)" == "$before_tree" ]]
rg -q 'retaining validated cached bundle assembly-providers-202-1' "$tmp/download.err"
echo "ok   failed provider download retains the validated cache"

# No cache plus a failed provider lookup gives a clear error and writes no descriptor or staged contents.
if env HOME="$tmp/home" PATH="$tmp/bin:/usr/bin:/bin" FAKE_RELEASES_FILE="$tmp/releases.json" FAKE_ASSET_ROOT="$tmp/releases" FAKE_FAIL_LIST=1 \
  bash "$here/../dotnet/scripts/agents-file.sh" "$tmp/fresh-adapters" "$tmp/fresh-agents.json" 2>"$tmp/fresh.err"; then
  echo "FAIL fresh offline startup unexpectedly succeeded"; exit 1
fi
rg -q 'no valid cached bundle exists' "$tmp/fresh.err"
[[ ! -e "$tmp/fresh-agents.json" ]]
[[ -z "$(find "$tmp/fresh-adapters/bundles" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null || true)" ]]
echo "ok   fresh offline startup fails clearly without writing saved state"

if env HOME="$tmp/home" PATH="$tmp/bin:/usr/bin:/bin" FAKE_RELEASES_FILE="$tmp/releases.json" FAKE_ASSET_ROOT="$tmp/releases" FAKE_FAIL_DOWNLOAD=1 \
  bash "$here/../dotnet/scripts/agents-file.sh" "$tmp/fresh-download-adapters" "$tmp/fresh-download-agents.json" 2>"$tmp/fresh-download.err"; then
  echo "FAIL fresh download failure unexpectedly succeeded"; exit 1
fi
rg -q 'no valid cached bundle exists' "$tmp/fresh-download.err"
[[ ! -e "$tmp/fresh-download-agents.json" ]]
[[ -z "$(find "$tmp/fresh-download-adapters/bundles" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null || true)" ]]
echo "ok   fresh failed download leaves no descriptor or staged bundle"

# A truncated archive with a matching test-manifest checksum fails staged extraction and retains the old cache.
python3 - "$tmp/releases.json" <<'PY'
import json, sys
p=sys.argv[1]; releases=json.load(open(p)); releases.append({"tagName":"assembly-providers-206-1","isDraft":False,"isPrerelease":False,"publishedAt":"2026-11-10T00:00:00Z","assets":["assembly-adapters-linux-x64.tar.gz","assembly-adapters-linux-x64.json"]}); json.dump(releases,open(p,"w"))
PY
if ! run_agents 2>"$tmp/truncated.err"; then echo "FAIL truncated archive fallback: $(cat "$tmp/truncated.err")"; exit 1; fi
[[ "$(sha256sum "$tmp/agents.json" | cut -d' ' -f1)" == "$before_descriptor" ]]
[[ "$(cat "$active_bundle/claude-launcher.ts")" == "export const launcher = 'selected';" ]]
rg -q 'retaining validated cached bundle assembly-providers-202-1' "$tmp/truncated.err"
[[ -z "$(find "$tmp/adapters/bundles" -maxdepth 1 -type d -name '.install.*' -print -quit)" ]]
[[ -z "$(find "$tmp/adapters/bundles" -maxdepth 1 -type d -name 'assembly-providers-206-1-*' -print -quit)" ]]
echo "ok   truncated archive leaves active bundle and descriptor intact"

# A valid new bundle plus malformed user TOML fails descriptor preparation before any publication.
python3 - "$tmp/releases.json" <<'PY'
import json, sys
p=sys.argv[1]; releases=json.load(open(p)); releases.append({"tagName":"assembly-providers-207-1","isDraft":False,"isPrerelease":False,"publishedAt":"2026-11-11T00:00:00Z","assets":["assembly-adapters-linux-x64.tar.gz","assembly-adapters-linux-x64.json"]}); json.dump(releases,open(p,"w"))
PY
printf '[mcp_servers.alpha\n' >"$tmp/home/.codex/config.toml"
if run_agents 2>"$tmp/toml.err"; then echo "FAIL malformed TOML unexpectedly succeeded"; exit 1; fi
[[ "$(sha256sum "$tmp/agents.json" | cut -d' ' -f1)" == "$before_descriptor" ]]
[[ "$(cat "$active_bundle/claude-launcher.ts")" == "export const launcher = 'selected';" ]]
rg -q 'TOMLDecodeError' "$tmp/toml.err"
[[ -z "$(find "$tmp/adapters/bundles" -maxdepth 1 -type d -name '.install.*' -print -quit)" ]]
[[ -z "$(find "$tmp/adapters/bundles" -maxdepth 1 -type d -name 'assembly-providers-207-1-*' -print -quit)" ]]
echo "ok   malformed TOML preserves prior bundle and descriptor byte-for-byte"

# A later complete release replaces rather than overlays the upstream tree. The old active bundle remains usable.
python3 - "$tmp/releases.json" <<'PY'
import json, sys
p=sys.argv[1]; releases=json.load(open(p)); releases.append({"tagName":"assembly-providers-208-1","isDraft":False,"isPrerelease":False,"publishedAt":"2026-11-12T00:00:00Z","assets":["assembly-adapters-linux-x64.tar.gz","assembly-adapters-linux-x64.json"]}); json.dump(releases,open(p,"w"))
PY
printf '[mcp_servers.alpha]\ncommand = "alpha"\n' >"$tmp/home/.codex/config.toml"
run_agents
new_bundle=$(python3 - "$tmp/agents.json" <<'PY'
import json, os, sys
agents={a["id"]:a for a in json.load(open(sys.argv[1]))}; print(os.path.dirname(agents["claude"]["args"][0]))
PY
)
[[ "$(cat "$new_bundle/provider-tag")" == assembly-providers-208-1 ]]
[[ ! -e "$new_bundle/obsolete-upstream-file.txt" ]]
[[ -e "$active_bundle/obsolete-upstream-file.txt" ]]
[[ "$(cat "$new_bundle/claude-launcher.ts")" == "export const launcher = 'replacement';" ]]
echo "ok   atomic version switch removes obsolete upstream files and retains previous bundle"
