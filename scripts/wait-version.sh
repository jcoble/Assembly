#!/usr/bin/env bash
# Waits for a backend to answer /version with the code of a commit: that commit, or one whose dotnet, app and shell
# are the same (only scripts or docs moved). A host that's still starting, or an old process that hasn't stopped yet,
# isn't taken for it. The token comes on stdin. Prints the commit that answered; after the deadline, prints one line
# naming the port and what it last saw, and fails.
#   scripts/wait-version.sh <name> <base url> <commit> [seconds, default 30]
set -uo pipefail
cd "$(dirname "$0")/.."
name=$1 base=$2 commit=$3 deadline=${4:-30}
IFS= read -r token || true

same_code() { [ "$1" = "$commit" ] || { git cat-file -e "$1^{commit}" 2>/dev/null && git diff --quiet "$1" "$commit" -- dotnet app shell; }; }

last="no answer"
end=$((SECONDS + deadline))
while :; do
  seen=$(printf 'Authorization: Bearer %s\n' "$token" | curl -s --max-time 5 -H @- "$base/version" | jq -r '.commit // empty' 2>/dev/null || true)
  if [ -n "$seen" ]; then
    same_code "$seen" && { echo "$seen"; exit 0; }
    last="it answered with $seen"
  fi
  [ "$SECONDS" -lt "$end" ] || break
  sleep 1
done
echo "$name on port ${base##*:} didn't answer /version with the code of ${commit:0:7} in $deadline s; last: $last"
exit 1
