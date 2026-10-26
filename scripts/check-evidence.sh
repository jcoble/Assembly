#!/usr/bin/env bash
# Fails if a code commit in base..head has no passing four-way check (scripts/check4.sh): a summary at
# evidence/<commit>/four-way/summary.json in head, with "pass": true, for that commit or a later one that contains it,
# that ran every feature scenario present at its own commit (scripts/features/*.sh). A partial run doesn't count.
# A code commit touches the backend's or the app's code: the code folders and files below, including the package
# versions and manifests (dotnet/Directory.*.props, app/package.json and its lockfile, the shell's Cargo files). Every
# dotnet/Assembly.* folder in head must be listed as code or not code, so a new project can't slip past the check.
# Runs on every push to main (.github/workflows/pinned.yml) and locally (default origin/main..HEAD).
set -euo pipefail
cd "$(dirname "$0")/.."
base=${1:-origin/main} head=${2:-HEAD}
# Assembly.Engine was split into Domain, Application and Infrastructure in B2c; it stays so the commits before that
# still count as code.
code=(dotnet/Assembly.Domain dotnet/Assembly.Application dotnet/Assembly.Infrastructure dotnet/Assembly.Mac
  dotnet/Assembly.Remote dotnet/Assembly.Engine app/src shell/src
  dotnet/Directory.Build.props dotnet/Directory.Packages.props app/package.json app/pnpm-lock.yaml
  shell/Cargo.toml shell/Cargo.lock)
not_code=(dotnet/Assembly.Tests)

failed=0
for d in $(git ls-tree -d --name-only "$head" dotnet/ | grep '^dotnet/Assembly\.' || true); do
  if [[ " ${code[*]} ${not_code[*]} " != *" $d "* ]]; then
    echo "$d is in neither code nor not_code in scripts/check-evidence.sh" >&2
    failed=1
  fi
done

# Commits with a passing, complete summary in head: one that ran every scenario present at that commit.
passing=() incomplete=()
for f in $(git ls-tree -r --name-only "$head" -- evidence | grep -E '^evidence/[0-9a-f]{40}/four-way/summary\.json$' || true); do
  line=$(git show "$head:$f" | jq -r 'select(.pass == true and .partial != true) | "\(.commit) \(.features // [] | join(" "))"' 2>/dev/null || true)
  [ -n "$line" ] || continue
  read -r p ran <<<"$line"
  git cat-file -e "$p^{commit}" 2>/dev/null || continue
  missing=""
  for s in $(git ls-tree --name-only "$p" scripts/features/ | sed -nE 's#^scripts/features/(.+)\.sh$#\1#p'); do
    grep -qw -- "$s" <<<" $ran " || missing+=" $s"
  done
  if [ -z "$missing" ]; then passing+=("$p"); else incomplete+=("${p:0:7} didn't run:$missing"); fi
done

for c in $(git rev-list --no-merges "$base..$head" -- "${code[@]}"); do
  covered=""
  for p in ${passing[@]+"${passing[@]}"}; do
    git merge-base --is-ancestor "$c" "$p" && { covered=$p; break; }
  done
  if [ -z "$covered" ]; then
    echo "code commit without a passing four-way check that ran every scenario: $(git log -1 --format='%h %s' "$c")" >&2
    failed=1
  fi
done
[ "$failed" = 1 ] && for i in ${incomplete[@]+"${incomplete[@]}"}; do echo "  (passing summary for $i)" >&2; done
[ "$failed" = 0 ] && echo "four-way checks: every code commit in $base..$head is covered"
exit "$failed"
