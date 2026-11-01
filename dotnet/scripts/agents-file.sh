#!/usr/bin/env bash
# dotnet/scripts/agents-file.sh ADAPTERS OUT
# Installs the latest published platform ACP bundle under ADAPTERS and writes OUT atomically.
set -euo pipefail
adapters=$1 out=$2
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) target=darwin-arm64 ;;
  Darwin-x86_64) target=darwin-x64 ;;
  Linux-x86_64) target=linux-x64 ;;
  *) echo "Unsupported adapter platform: $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac
asset="assembly-adapters-$target.tar.gz"
manifest_file="assembly-adapters-$target.json"
mkdir -p "$adapters" "$(dirname "$out")"
adapters=$(cd "$adapters" && pwd)
bundle_root="$adapters/bundles"
mkdir -p "$bundle_root"
out_dir=$(cd "$(dirname "$out")" && pwd)
out="$out_dir/$(basename "$out")"
work=$(mktemp -d "$bundle_root/.install.XXXXXX")
descriptor_tmp=
published_bundle=
remove_published_bundle=
cleanup() {
  rm -rf "$work"
  [ -z "$descriptor_tmp" ] || rm -f "$descriptor_tmp"
  [ -z "$remove_published_bundle" ] || rm -rf "$remove_published_bundle"
}
trap cleanup EXIT

# The descriptor itself identifies the active immutable bundle. Validate its exact launcher and Codex paths
# against the bundle manifest and installed package versions before allowing it to serve as an offline cache.
cache_state=$(python3 -I - "$out" "$bundle_root" "$target" <<'PY' 2>/dev/null || true
import json, os, re, sys
out, root, target = sys.argv[1:]
try:
    agents = {a["id"]: a for a in json.load(open(out, encoding="utf-8"))}
    claude, codex = agents["claude"], agents["codex"]
    launcher = claude["args"][0]
    bundle = os.path.dirname(launcher)
    root = os.path.realpath(root)
    bundle_real = os.path.realpath(bundle)
    if os.path.commonpath([root, bundle_real]) != root or launcher != os.path.join(bundle_real, "claude-launcher.ts"):
        raise ValueError("Claude entry is outside the adapters bundle cache")
    expected_codex = os.path.join(bundle_real, "node_modules/@agentclientprotocol/codex-acp/dist/index.js")
    if codex["args"][0] != expected_codex:
        raise ValueError("Codex entry does not match the cached bundle")
    manifest = json.load(open(os.path.join(bundle_real, "provider-manifest.json"), encoding="utf-8"))
    tag = open(os.path.join(bundle_real, "provider-tag"), encoding="utf-8").read().strip()
    if not re.fullmatch(r"assembly-providers-[0-9]+-[0-9]+", tag) or manifest.get("target") != target:
        raise ValueError("Cached provider identity is invalid")
    if manifest.get("schemaVersion") != 1 or manifest.get("entrypoints", {}).get("claude-agent-acp") != "claude-launcher.ts":
        raise ValueError("Cached manifest is invalid")
    if manifest.get("entrypoints", {}).get("codex-acp") != "node_modules/@agentclientprotocol/codex-acp/dist/index.js":
        raise ValueError("Cached Codex manifest entry is invalid")
    versions = manifest["versions"]
    for name in ("claude-agent-acp", "codex-acp"):
        smoke = manifest.get("smoke", {}).get(name, {})
        if smoke.get("protocolVersion") != 2 or smoke.get("info", {}).get("version") != versions[name] or smoke.get("canSteer") is not True:
            raise ValueError("Cached smoke proof is invalid")
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", versions["claude-agent-acp"]):
        raise ValueError("Cached Claude version is invalid")
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", versions["codex-acp"]):
        raise ValueError("Cached Codex version is invalid")
    for relative, package, version in [
        ("claude-launcher.ts", None, None),
        ("package.json", None, None),
        ("package-lock.json", None, None),
        ("node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js", "claude-agent-acp", versions["claude-agent-acp"]),
        ("node_modules/@agentclientprotocol/codex-acp/dist/index.js", "codex-acp", versions["codex-acp"]),
    ]:
        path = os.path.join(bundle_real, relative)
        if not os.path.isfile(path): raise ValueError("Cached bundle file is missing: " + relative)
        if package:
            metadata = json.load(open(os.path.join(bundle_real, "node_modules/@agentclientprotocol", package, "package.json"), encoding="utf-8"))
            if metadata.get("version") != version: raise ValueError("Cached package version mismatch: " + package)
    print(tag + "\t" + bundle_real)
except Exception:
    sys.exit(1)
PY
)
active_tag= active_bundle=
if [[ -n "$cache_state" ]]; then IFS=$'\t' read -r active_tag active_bundle <<<"$cache_state"; fi

cached_fallback() {
  local reason=$1
  if [[ -n "$active_bundle" ]]; then
    echo "adapter update unavailable ($reason); retaining validated cached bundle $active_tag" >&2
    exit 0
  fi
  echo "adapter update unavailable ($reason), and no valid cached bundle exists" >&2
  exit 1
}

if ! tag=$(node "$repo/scripts/package-adapters.ts" provider-tag "$target" 2>"$work/provider-selection.err"); then
  cached_fallback "$(tail -1 "$work/provider-selection.err")"
fi
if [[ "$tag" == "$active_tag" ]]; then
  bundle=$active_bundle
else
  download="$work/download"
  stage="$work/bundle"
  mkdir "$download" "$stage"
  if ! gh release download "$tag" --repo jcoble/Assembly --pattern "$asset" --pattern "$manifest_file" --dir "$download" 2>"$work/provider-download.err"; then
    cached_fallback "$(tail -1 "$work/provider-download.err")"
  fi
  if ! python3 -I - "$download/$manifest_file" "$download/$asset" "$target" <<'PY' 2>"$work/manifest.err"
import hashlib, json, re, sys
manifest_path, archive_path, target = sys.argv[1:]
manifest = json.load(open(manifest_path, encoding="utf-8"))
expected_archive = "assembly-adapters-" + target + ".tar.gz"
if manifest.get("schemaVersion") != 1 or manifest.get("target") != target or manifest.get("archive", {}).get("file") != expected_archive:
    raise ValueError("provider manifest target mismatch")
entries = manifest.get("entrypoints", {})
if entries.get("claude-agent-acp") != "claude-launcher.ts" or entries.get("codex-acp") != "node_modules/@agentclientprotocol/codex-acp/dist/index.js":
    raise ValueError("provider manifest entrypoint mismatch")
versions = manifest.get("versions", {})
for name in ("claude-agent-acp", "codex-acp"):
    if not isinstance(versions.get(name), str) or not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", versions[name]):
        raise ValueError("provider manifest has invalid version: " + name)
    smoke = manifest.get("smoke", {}).get(name, {})
    if smoke.get("protocolVersion") != 2 or smoke.get("info", {}).get("version") != versions[name] or smoke.get("canSteer") is not True:
        raise ValueError("provider manifest has invalid smoke proof: " + name)
checksum = hashlib.sha256(open(archive_path, "rb").read()).hexdigest()
if manifest.get("archive", {}).get("sha256") != checksum:
    raise ValueError("provider archive checksum mismatch")
PY
  then
    cached_fallback "$(tail -1 "$work/manifest.err")"
  fi
  if ! tar -tzf "$download/$asset" >"$work/archive-members" 2>"$work/archive.err"; then
    cached_fallback "$(tail -1 "$work/archive.err")"
  fi
  if ! python3 -I - "$work/archive-members" <<'PY'
import sys
members = [line.strip().rstrip("/") for line in open(sys.argv[1], encoding="utf-8")]
if any(name.startswith("/") or ".." in name.split("/") for name in members):
    raise ValueError("provider archive contains an unsafe path")
required = ["package.json", "package-lock.json", "claude-launcher.ts",
            "node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js",
            "node_modules/@agentclientprotocol/codex-acp/dist/index.js"]
missing = [name for name in required if name not in members]
if missing: raise ValueError("provider archive is missing required entries: " + ", ".join(missing))
PY
  then
    cached_fallback "provider archive entry validation failed"
  fi
  if ! tar -xzf "$download/$asset" -C "$stage" 2>"$work/extract.err"; then
    cached_fallback "$(tail -1 "$work/extract.err")"
  fi
  cp "$download/$manifest_file" "$stage/provider-manifest.json"
  printf '%s\n' "$tag" >"$stage/provider-tag"
  if ! python3 -I - "$stage" "$target" <<'PY'
import json, os, re, sys
bundle, target = sys.argv[1:]
manifest = json.load(open(os.path.join(bundle, "provider-manifest.json"), encoding="utf-8"))
if manifest.get("target") != target: raise ValueError("staged bundle target mismatch")
versions = manifest["versions"]
for relative, package in [
    ("claude-launcher.ts", None), ("package.json", None), ("package-lock.json", None),
    ("node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js", "claude-agent-acp"),
    ("node_modules/@agentclientprotocol/codex-acp/dist/index.js", "codex-acp"),
]:
    path = os.path.join(bundle, relative)
    if not os.path.isfile(path): raise ValueError("staged bundle is missing " + relative)
    if package:
        metadata = json.load(open(os.path.join(bundle, "node_modules/@agentclientprotocol", package, "package.json"), encoding="utf-8"))
        if metadata.get("version") != versions[package]: raise ValueError("staged version mismatch: " + package)
PY
  then
    cached_fallback "staged bundle validation failed"
  fi
  stage_id=${work##*/}
  stage_id=${stage_id#.install.}
  final_bundle="$bundle_root/$tag-$stage_id"
  bundle=$final_bundle
fi

# Render and parse the replacement descriptor before publishing either artifact. A bad TOML file or an
# invalid descriptor leaves the old descriptor and all referenced bundle files untouched.
descriptor_tmp=$(mktemp "$out_dir/.$(basename "$out").tmp.XXXXXX")
ASSEMBLY_NODE=$(command -v node) ASSEMBLY_CLAUDE=$(command -v claude || true) ASSEMBLY_CODEX=$(command -v codex || true) \
  ASSEMBLY_ADAPTERS=$bundle ASSEMBLY_USER_MCP=${ASSEMBLY_USER_MCP:-off} \
  ASSEMBLY_CLAUDEAI_MCP=${ASSEMBLY_CLAUDEAI_MCP:-off} python3 -I - >"$descriptor_tmp" <<'PY'
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
agents = [
    {"id": "claude", "name": "Claude Code", "command": node, "args": [f"{adapters}/claude-launcher.ts"], "env": claude_env},
    {"id": "codex", "name": "Codex", "command": node, "args": [acp("codex-acp")], "env": {"CODEX_CONFIG": json.dumps(codex)}},
]
for agent in agents:
    if cli := os.environ[f"ASSEMBLY_{agent['id'].upper()}"]:
        agent["cli"] = cli
print(json.dumps(agents, indent=2))
PY
python3 -I - "$descriptor_tmp" "$bundle" <<'PY'
import json, os, sys
path, bundle = sys.argv[1:]
agents = {agent["id"]: agent for agent in json.load(open(path, encoding="utf-8"))}
expected = {
    "claude": os.path.join(bundle, "claude-launcher.ts"),
    "codex": os.path.join(bundle, "node_modules/@agentclientprotocol/codex-acp/dist/index.js"),
}
for name, entry in expected.items():
    if agents[name]["args"] != [entry]: raise ValueError("descriptor entry mismatch: " + name)
if "air" in agents["codex"]: raise ValueError("Codex descriptor must not opt into AIR")
PY

if [[ "$bundle" != "$active_bundle" ]]; then
  mv "$stage" "$final_bundle"
  remove_published_bundle=$final_bundle
  bundle=$final_bundle
fi
mv -f "$descriptor_tmp" "$out"
descriptor_tmp=
remove_published_bundle=

# Keep only the just-active immutable tree and its immediately previous active tree. Older directories are our
# own provider bundles and no longer have a descriptor pointing to them.
for old_bundle in "$bundle_root"/assembly-providers-*; do
  [[ -d "$old_bundle" ]] || continue
  if [[ "$old_bundle" != "$bundle" && "$old_bundle" != "$active_bundle" ]]; then
    rm -rf "$old_bundle" || echo "could not remove inactive adapter bundle $old_bundle" >&2
  fi
done
