#!/usr/bin/env bash
# Every .NET file is formatted as dotnet/.editorconfig says (plan 9.2): dotnet format --verify-no-changes, naming each
# spot that isn't; `dotnet format dotnet/Assembly.slnx` fixes them. CI and a plain scripts/lint.sh run it on every tree.
# The commit hook sets LINT_STAGED_ONLY=1, and then it runs only when the commit changes a .NET file, so lanes
# committing at once don't each start a .NET build. That's scoping, not an allow list: CI still checks everything.
cd "$(dirname "$0")/../.."
if [ -n "${LINT_STAGED_ONLY:-}" ] && ! git diff --cached --name-only | grep -qE '^dotnet/|\.(cs|csproj|props|editorconfig)$'; then
  echo "dotnet format: skipped, the commit changes no .NET file"
  exit 0
fi
command -v dotnet >/dev/null || { echo "dotnet format: no dotnet on PATH; refusing" >&2; exit 2; }
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
out=$(dotnet format dotnet/Assembly.slnx --verify-no-changes -v q 2>&1) && {
  echo "dotnet format: every .NET file is formatted"
  exit 0
}
echo "$out" >&2
echo "dotnet format: run dotnet format dotnet/Assembly.slnx to fix the spots above" >&2
exit 1
