#!/usr/bin/env bash
# dotnet/scripts/agents-file.sh ADAPTERS OUT [LOCAL_ASSETS]
# Installs the latest published platform ACP bundle under ADAPTERS and writes OUT atomically.
# Releases are public and read with curl: Workbox Test runs this with no gh and no GitHub login.
# An explicit local packager output uses the same validation with archive-hash provenance, never a release tag.
set -euo pipefail
adapters=$1 out=$2
local_assets=${3:-}
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
out=$(python3 -I - "$out" <<'PY'
import os, sys
path = os.path.realpath(sys.argv[1])
if not os.path.isdir(os.path.dirname(path)):
    raise ValueError("agents file physical parent is unavailable")
print(path)
PY
)
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
    published = re.fullmatch(r"assembly-providers-[0-9]+-[0-9]+", tag)
    local = re.fullmatch(r"local-sha256-[0-9a-f]{64}", tag)
    if not (published or local) or manifest.get("target") != target:
        raise ValueError("Cached provider identity is invalid")
    if local and tag != "local-sha256-" + manifest.get("archive", {}).get("sha256", ""):
        raise ValueError("Cached local archive identity is invalid")
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

# Node and curl lead with the cause; the end of a Node stack dump is only punctuation.
first_error() { grep -m1 -E 'Error:|^curl:' "$1" || grep -m1 . "$1" || echo "no error output"; }
cached_fallback() {
  local reason=$1
  if [[ -n "$active_bundle" ]]; then
    echo "adapter update unavailable ($reason); retaining validated cached bundle $active_tag" >&2
    exit 0
  fi
  echo "adapter update unavailable ($reason), and no valid cached bundle exists" >&2
  exit 1
}

if [[ -n "$local_assets" ]]; then
  if ! local_assets=$(cd "$local_assets" && pwd); then cached_fallback "local assets directory unavailable"; fi
  if ! tag=$(python3 -I - "$local_assets/$asset" <<'PY' 2>"$work/provider-selection.err"
import hashlib, sys
with open(sys.argv[1], "rb") as archive:
    print("local-sha256-" + hashlib.file_digest(archive, "sha256").hexdigest())
PY
  ); then cached_fallback "$(tail -1 "$work/provider-selection.err")"; fi
elif ! tag=$(node "$repo/scripts/package-adapters.ts" provider-tag "$target" 2>"$work/provider-selection.err"); then
  cached_fallback "$(first_error "$work/provider-selection.err")"
fi
if [[ "$tag" == "$active_tag" && -z "$local_assets" ]]; then
  bundle=$active_bundle
else
  download="$work/download"
  stage="$work/bundle"
  mkdir "$download" "$stage"
  if [[ -n "$local_assets" ]]; then
    if ! cp "$local_assets/$asset" "$local_assets/$manifest_file" "$download/" 2>"$work/provider-download.err"; then
      cached_fallback "$(tail -1 "$work/provider-download.err")"
    fi
  else
    release_url="https://github.com/jcoble/Assembly/releases/download/$tag"
    if ! curl -fsSL --connect-timeout 10 -o "$download/$asset" "$release_url/$asset" 2>"$work/provider-download.err" ||
      ! curl -fsSL --connect-timeout 10 -o "$download/$manifest_file" "$release_url/$manifest_file" 2>"$work/provider-download.err"; then
      cached_fallback "$(first_error "$work/provider-download.err")"
    fi
  fi
  if ! python3 -I - "$download/$manifest_file" "$download/$asset" "$target" "$tag" <<'PY' 2>"$work/manifest.err"
import hashlib, json, re, sys
manifest_path, archive_path, target, tag = sys.argv[1:]
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
if tag.startswith("local-sha256-") and tag != "local-sha256-" + checksum:
    raise ValueError("local provider archive changed during staging")
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

# Downloads and bundle validation above do not hold the descriptor writer lock.
# The permanent physical-file sidecar is shared with the runtime settings writer.
ASSEMBLY_NODE=$(command -v node) ASSEMBLY_CLAUDE=$(command -v claude || true) ASSEMBLY_CODEX=$(command -v codex || true) \
  ASSEMBLY_ADAPTERS=$bundle ASSEMBLY_USER_MCP=${ASSEMBLY_USER_MCP:-off} \
  ASSEMBLY_CLAUDEAI_MCP=${ASSEMBLY_CLAUDEAI_MCP:-off} python3 -I - "$out" "$bundle_root" "$bundle" "${stage:-}" "${final_bundle:-}" <<'PY'
import fcntl, hashlib, json, os, shutil, sys, tempfile, tomllib
out, root, bundle, stage, final_bundle = sys.argv[1:]
root = os.path.realpath(root)
lock = os.open(out + ".lock", os.O_CREAT | os.O_RDWR, 0o600)
temporary = None
moved = published = False
try:
    try: fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError: sys.exit("agents file unavailable: writer busy")
    def read_source():
        try:
            with open(out, "rb") as source: return source.read()
        except FileNotFoundError: return None
    original = read_source()
    revision = hashlib.sha256(original).hexdigest() if original is not None else None
    current = json.loads(original) if original is not None else []
    if not isinstance(current, list) or any(not isinstance(a, dict) or not isinstance(a.get("id"), str) for a in current):
        raise ValueError("invalid current agents file")
    prior = {a["id"]: a for a in current}
    if len(prior) != len(current): raise ValueError("duplicate configured provider identity")
    old_codex_env = prior.get("codex", {}).get("env")
    if old_codex_env is None: old_codex_env = {}
    if not isinstance(old_codex_env, dict): raise ValueError("invalid configured provider environment")
    codex = {"model": "gpt-5.6-luna", "tools": {"update_plan": {"enabled": True}}}
    if os.environ["ASSEMBLY_USER_MCP"] == "off":
        codex_home = old_codex_env.get("CODEX_HOME") if "CODEX_HOME" in old_codex_env else os.environ.get("CODEX_HOME")
        if codex_home is None and "CODEX_HOME" not in old_codex_env:
            codex_home = os.path.expanduser("~/.codex")
        elif codex_home == "":
            raise ValueError("CODEX_HOME must not be empty when configured")
        if not isinstance(codex_home, str): raise ValueError("invalid configured provider home")
        path = os.path.join(codex_home, "config.toml")
        config = tomllib.load(open(path, "rb")) if os.path.exists(path) else {}
        codex["mcp_servers"] = {name: {"enabled": False} for name in sorted(config.get("mcp_servers", {}))}
    claude_env = {"CLAUDE_CODE_ENABLE_TODO_TOOLS": "1"}
    if os.environ["ASSEMBLY_CLAUDEAI_MCP"] == "off": claude_env["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false"
    node, adapters = os.environ["ASSEMBLY_NODE"], os.environ["ASSEMBLY_ADAPTERS"]
    generated = [
        {"id": "claude", "vendorFamily": "claude", "name": "Claude Code", "command": node,
         "args": [os.path.join(adapters, "claude-launcher.ts")], "env": claude_env},
        {"id": "codex", "vendorFamily": "codex", "name": "Codex", "command": node,
         "args": [os.path.join(adapters, "node_modules/@agentclientprotocol/codex-acp/dist/index.js")], "env": {"CODEX_CONFIG": json.dumps(codex)}},
    ]
    installer_fields = {"id", "vendorFamily", "name", "command", "args", "env"}
    installer_env = {"codex": {"CODEX_CONFIG"}, "claude": {"CLAUDE_CODE_ENABLE_TODO_TOOLS", "ENABLE_CLAUDEAI_MCP_SERVERS"}}
    for agent in generated:
        old = prior.get(agent["id"])
        if old is None:
            if cli := os.environ[f"ASSEMBLY_{agent['id'].upper()}"]: agent["cli"] = cli
            continue
        old_env = old.get("env")
        if old_env is None: old_env = {}
        if not isinstance(old_env, dict): raise ValueError("invalid configured provider environment")
        agent["env"] = {**{key: value for key, value in old_env.items() if key not in installer_env[agent["id"]]}, **agent["env"]}
        agent.update({key: value for key, value in old.items() if key not in installer_fields})
    generated_by_id = {a["id"]: a for a in generated}
    agents = [generated_by_id.get(a["id"], a) for a in current]
    agents.extend(a for a in generated if a["id"] not in prior)
    encoded = (json.dumps(agents, indent=2) + "\n").encode("utf-8")
    validated = {a["id"]: a for a in json.loads(encoded)}
    for name, relative in [("claude", "claude-launcher.ts"), ("codex", "node_modules/@agentclientprotocol/codex-acp/dist/index.js")]:
        if validated[name]["args"] != [os.path.join(bundle, relative)]: raise ValueError("descriptor entry mismatch")
        if not os.path.isfile(os.path.join(stage if final_bundle else bundle, relative)): raise ValueError("validated bundle is unavailable")
    if "air" in validated["codex"]: raise ValueError("Codex descriptor must not opt into AIR")
    descriptor, temporary = tempfile.mkstemp(prefix="." + os.path.basename(out) + ".tmp.", dir=os.path.dirname(out))
    with os.fdopen(descriptor, "wb") as output:
        output.write(encoded); output.flush(); os.fsync(output.fileno())
    latest = read_source()
    if revision != (hashlib.sha256(latest).hexdigest() if latest is not None else None):
        raise ValueError("agents file changed before publication")
    if final_bundle:
        os.rename(stage, final_bundle); moved = True
    os.replace(temporary, out); temporary = None; published = True
    directory = os.open(os.path.dirname(out), os.O_RDONLY)
    try: os.fsync(directory)
    finally: os.close(directory)
    # Retain all bundles referenced by either descriptor, including unrelated providers.
    keep = {os.path.realpath(bundle)}
    for agent in current + agents:
        for value in [agent.get("command"), *(agent.get("args") or [])]:
            if not isinstance(value, str): continue
            relative = os.path.relpath(os.path.realpath(value), root)
            first = relative.split(os.sep)[0]
            if first not in (".", ".."): keep.add(os.path.join(root, first))
    for name in os.listdir(root):
        old = os.path.join(root, name)
        if name.startswith(("assembly-providers-", "local-sha256-")) and os.path.isdir(old) and not os.path.islink(old) and old not in keep:
            try: shutil.rmtree(old)
            except OSError: print("could not remove inactive adapter bundle", file=sys.stderr)
finally:
    if temporary is not None: os.unlink(temporary)
    if moved and not published: shutil.rmtree(final_bundle)
    os.close(lock)
PY
