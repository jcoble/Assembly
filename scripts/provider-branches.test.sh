#!/usr/bin/env bash
# Exercises the real guard against an isolated tracked tree, including its two exact adapter boundaries.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(mktemp -d)
trap 'rm -rf "$repo"' EXIT
git -C "$repo" init -q
mkdir -p "$repo/scripts/lint.d" "$repo/dotnet/Assembly.Application/Common/Agents" "$repo/dotnet/Assembly.Application/Common/Acp" "$repo/dotnet/Assembly.Application/Features/Agents"
cp "$here/lint.d/30-provider-branches.sh" "$repo/scripts/lint.d/"
metadata='var home = agent.VendorFamily switch { "codex" => "CODEX_HOME", "claude" => "CLAUDE_CONFIG_DIR", _ => null };'
printf '%s\n' "$metadata" > "$repo/dotnet/Assembly.Application/Common/Agents/AccountContext.cs"
printf '%s\n' 'var glyph = name switch { "codex" => "icon", _ => "other" };' > "$repo/dotnet/Assembly.Application/Common/Acp/ProviderGlyph.cs"
expect() {
  git -C "$repo" add -A
  local got=0
  bash "$repo/scripts/lint.d/30-provider-branches.sh" > "$repo/result" 2>&1 || got=$?
  if [[ "$got" != "$1" ]]; then cat "$repo/result"; echo "FAIL $2: expected $1, got $got"; exit 1; fi
  echo "ok   $2"
}
expect 0 'exact icon and explicit VendorFamily metadata boundaries pass'
printf '%s\n' "$metadata" > "$repo/dotnet/Assembly.Application/Common/Agents/Other.cs"
expect 1 'the same VendorFamily branch in a neighboring file remains rejected'
rm "$repo/dotnet/Assembly.Application/Common/Agents/Other.cs"
printf '%s\n' "$metadata" > "$repo/dotnet/Assembly.Application/Features/Agents/AccountContext.cs"
expect 1 'the old Features path is not exempt'
rm "$repo/dotnet/Assembly.Application/Features/Agents/AccountContext.cs"
printf '%s\n' 'if (agent.Id == "claude") Run();' > "$repo/dotnet/Assembly.Application/Common/Agents/Other.cs"
expect 1 'agent ID branching elsewhere remains rejected'
rm "$repo/dotnet/Assembly.Application/Common/Agents/Other.cs"
expect 0 'removing forbidden branches restores a passing tree'
