#!/usr/bin/env bash
# Fails if a feature commit in base..head has no passing four-way check (scripts/check4.sh): a summary at
# evidence/<commit>/four-way/summary.json in head, with "pass": true, for that commit or a later one that contains it.
# A feature commit touches the backend's or the app's code: dotnet/Assembly.{Engine,Mac,Remote}/, app/src/, shell/src/.
# If it touches a feature's folder (dotnet/Assembly.Engine/<Feature>/ or app/src/lib/<feature>/, other than those in
# scripts/features/base-folders), the summary must also have run that feature's scenario (check4.sh --feature).
# Runs on every push to main (.github/workflows/pinned.yml) and locally (default origin/main..HEAD).
set -euo pipefail
cd "$(dirname "$0")/.."
base=${1:-origin/main} head=${2:-HEAD}
code=(dotnet/Assembly.Engine dotnet/Assembly.Mac dotnet/Assembly.Remote app/src shell/src)
base_folders=$(git show "$head:scripts/features/base-folders" 2>/dev/null | grep -vE '^(#|$)' || true)

# Passing summaries in head: "<commit> <feature> <feature>...".
passing=()
for f in $(git ls-tree -r --name-only "$head" -- evidence | grep -E '^evidence/[0-9a-f]{40}/four-way/summary\.json$' || true); do
  line=$(git show "$head:$f" | jq -r 'select(.pass == true) | "\(.commit) \(.features // [] | join(" "))"' 2>/dev/null || true)
  [ -n "$line" ] && passing+=("$line")
done

# The features a commit touches, in lower case.
features_of() {
  git diff-tree --no-commit-id --name-only -r "$1" -- dotnet/Assembly.Engine app/src/lib \
    | sed -nE 's#^(dotnet/Assembly\.Engine|app/src/lib)/([^/]+)/.*#\1/\2#p' | sort -u \
    | while read -r folder; do grep -qxF "$folder" <<<"$base_folders" || basename "$folder" | tr 'A-Z' 'a-z'; done | sort -u
}

failed=0
for c in $(git rev-list --no-merges "$base..$head" -- "${code[@]}"); do
  needed=$(features_of "$c" | tr '\n' ' '); needed=${needed% }
  covered=""
  for entry in ${passing[@]+"${passing[@]}"}; do
    read -r p ran <<<"$entry"
    git cat-file -e "$p^{commit}" 2>/dev/null && git merge-base --is-ancestor "$c" "$p" || continue
    missing=""
    for f in $needed; do grep -qw -- "$f" <<<" $ran " || missing+=" $f"; done
    [ -z "$missing" ] && { covered=$p; break; }
  done
  if [ -z "$covered" ]; then
    echo "feature commit without a passing four-way check${needed:+ with scenarios for: $needed}: $(git log -1 --format='%h %s' "$c")" >&2
    failed=1
  fi
done
[ "$failed" = 0 ] && echo "four-way checks: every feature commit in $base..$head is covered"
exit "$failed"
