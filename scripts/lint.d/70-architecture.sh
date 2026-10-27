#!/usr/bin/env bash
# The architecture rules hold (plan 9.2): runs dotnet/Assembly.Tests/Architecture (which project references which, no
# file, SQLite or native calls in the core, namespaces match folders, folder sizes). CI and a plain scripts/lint.sh
# run it on every tree. The commit hook sets LINT_STAGED_ONLY=1, and then it runs only when the commit changes a .NET
# file, so lanes committing at once don't each start a .NET build. That's scoping, not an allow list: CI still checks
# everything.
cd "$(dirname "$0")/../.."
if [ -n "${LINT_STAGED_ONLY:-}" ] && ! git diff --cached --name-only | grep -qE '^dotnet/|\.(cs|csproj|props|editorconfig)$'; then
  echo "architecture: skipped, the commit changes no .NET file"
  exit 0
fi
command -v dotnet >/dev/null || { echo "architecture: no dotnet on PATH; refusing" >&2; exit 2; }
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# TreatNoTestsAsError: a filter that matches nothing fails, where VSTest would otherwise pass with nothing run.
out=$(dotnet test dotnet/Assembly.Tests --filter "FullyQualifiedName~Assembly.Tests.Architecture" -v q --nologo -- RunConfiguration.TreatNoTestsAsError=true 2>&1) && {
  echo "architecture: every architecture test passes"
  exit 0
}
echo "$out" >&2
echo "architecture: the architecture tests above fail" >&2
exit 1
