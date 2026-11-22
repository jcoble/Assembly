#!/usr/bin/env bash
# Every .NET file is formatted as dotnet/.editorconfig says (plan 9.2): dotnet format --verify-no-changes, naming each
# spot that isn't; `dotnet format <project or dotnet/Assembly.slnx>` fixes them. How much it checks:
#   the commit hook (LINT_STAGED_ONLY=1)   the projects holding the staged .cs/.csproj files, nothing when none is staged
#   a pull request (LINT_BASE=<base sha>)  the projects holding the .cs/.csproj files changed against that base
#   anything else (main, a plain lint.sh) the whole solution: the backstop that catches what a narrower run missed
# A change to something every project shares (an .editorconfig, a .props/.targets/.slnx, global.json) widens it to the
# whole solution. A project is the whole unit (not a file): dotnet format spends its time loading and analyzing the
# project, so --include one file is no faster. Not `dotnet format whitespace`: it skips the analyzer rules the
# .editorconfig sets (CA2016). A deleted file is not checked; a renamed one is, under its new name.
cd "$(dirname "$0")/../.."
scoped=1
if [ -n "${LINT_STAGED_ONLY:-}" ]; then
  changed=$(git -c core.quotePath=false diff --cached --name-only --diff-filter=ACMR) || exit 2
elif [ -n "${LINT_BASE:-}" ]; then
  changed=$(git -c core.quotePath=false diff --name-only --diff-filter=ACMR "$LINT_BASE" HEAD) || {
    echo "dotnet format: cannot diff against $LINT_BASE; refusing" >&2
    exit 2
  }
else
  scoped=
fi
targets=(dotnet/Assembly.slnx)
if [ -n "$scoped" ] && ! grep -qE '(^|/)(\.editorconfig|global\.json)$|\.(props|targets|slnx)$' <<<"$changed"; then
  targets=()
  while IFS= read -r file; do
    case $file in dotnet/*.cs | dotnet/*.csproj) ;; *) continue ;; esac
    dir=$(dirname "$file")
    while [ "$dir" != dotnet ] && ! compgen -G "$dir/*.csproj" >/dev/null; do dir=$(dirname "$dir"); done
    [ "$dir" != dotnet ] || continue
    project=$(compgen -G "$dir/*.csproj")
    case " ${targets[*]} " in *" $project "*) ;; *) targets+=("$project") ;; esac
  done <<<"$changed"
  [ "${#targets[@]}" -gt 0 ] || { echo "dotnet format: skipped, the change holds no .NET project file"; exit 0; }
fi
command -v dotnet >/dev/null || { echo "dotnet format: no dotnet on PATH; refusing" >&2; exit 2; }
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
failed=0
for target in "${targets[@]}"; do
  out=$(dotnet format "$target" --verify-no-changes -v q 2>&1) || {
    echo "$out" >&2
    echo "dotnet format: run dotnet format $target to fix the spots above" >&2
    failed=1
  }
done
[ "$failed" = 0 ] || exit 1
echo "dotnet format: every file in ${targets[*]} is formatted"
