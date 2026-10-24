#!/usr/bin/env bash
# Fails if a feature commit in base..head has no passing four-way check (scripts/check4.sh): a summary at
# evidence/<commit>/four-way/summary.json in head, with "pass": true, for that commit or a later one that contains it.
# A feature commit is one that touches the backend's or the app's code: dotnet/Assembly.{Engine,Mac,Remote}/, app/src/,
# shell/src/. Runs on every push to main (.github/workflows/pinned.yml) and locally (default origin/main..HEAD).
set -euo pipefail
cd "$(dirname "$0")/.."
base=${1:-origin/main} head=${2:-HEAD}
features=(dotnet/Assembly.Engine dotnet/Assembly.Mac dotnet/Assembly.Remote app/src shell/src)
checked=$(git ls-tree -r --name-only "$head" -- evidence | grep -E '^evidence/[0-9a-f]{40}/four-way/summary\.json$' || true)
passing=()
for f in $checked; do
  git show "$head:$f" | jq -e '.pass == true' >/dev/null 2>&1 && passing+=("$(cut -d/ -f2 <<<"$f")")
done
failed=0
for c in $(git rev-list --no-merges "$base..$head" -- "${features[@]}"); do
  covered=""
  for p in ${passing[@]+"${passing[@]}"}; do
    git cat-file -e "$p^{commit}" 2>/dev/null && git merge-base --is-ancestor "$c" "$p" && { covered=$p; break; }
  done
  if [ -z "$covered" ]; then
    echo "feature commit without a passing four-way check: $(git log -1 --format='%h %s' "$c")" >&2
    failed=1
  fi
done
[ "$failed" = 0 ] && echo "four-way checks: every feature commit in $base..$head is covered"
exit "$failed"
