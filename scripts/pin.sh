#!/usr/bin/env bash
# Moves a feature's draft tests into the pinned folders once Jesse has corrected its behavior list (a page in the
# Assembly documentation in Notion):
#   scripts/pin.sh <feature> <link to the behavior list page>
#                              dotnet/Assembly.Tests/Draft/<Feature>/ -> Pinned/<Feature>/ (namespace too), and
#                              staged for a commit of their own
# It cannot read Notion, so the person running it has checked that Jesse corrected the list; the link goes in the pin
# commit's message. It refuses if the link isn't a Notion page link, if a test doesn't name the list line it pins
# (// Pins: <feature>#<n>), or if a pinned file of the same name exists.
set -euo pipefail
cd "$(dirname "$0")/.."
feature=${1:?usage: scripts/pin.sh <feature> <link to the behavior list page>}
list=${2:?usage: scripts/pin.sh <feature> <link to the behavior list page>}
grep -Eq '^https://app[.]notion[.]com/p/[0-9a-fA-F]{32}([?].*)?$' <<<"$list" \
  || { echo "the second argument must be the behavior list's Notion page link, got: $list" >&2; exit 1; }

dirs=()
cs=$(find dotnet/Assembly.Tests/Draft -mindepth 1 -maxdepth 1 -type d -iname "$feature" 2>/dev/null | head -1)
[ -n "$cs" ] && dirs+=("$cs")
[ ${#dirs[@]} -gt 0 ] || { echo "no draft tests for $feature" >&2; exit 1; }

files=$(git ls-files -- "${dirs[@]}")
[ -n "$files" ] || { echo "no committed draft tests for $feature" >&2; exit 1; }
failed=0
for f in $files; do
  case "$f" in *.cs) grep -qE '^\s*\[(Fact|Theory|MacFact)\b' "$f" || continue ;; *) continue ;; esac
  grep -qE "Pins: $feature#[0-9]+" "$f" || { echo "$f doesn't name the line it pins (// Pins: $feature#<n>)" >&2; failed=1; }
done
for f in $files; do
  to=$(sed -E 's#^dotnet/Assembly\.Tests/Draft/#dotnet/Assembly.Tests/Pinned/#' <<<"$f")
  [ -e "$to" ] && { echo "$to already exists" >&2; failed=1; }
done
[ "$failed" = 0 ] || exit 1

for f in $files; do
  to=$(sed -E 's#^dotnet/Assembly\.Tests/Draft/#dotnet/Assembly.Tests/Pinned/#' <<<"$f")
  mkdir -p "$(dirname "$to")"
  git mv "$f" "$to"
  case "$to" in *.cs) sed -i.bak 's/Assembly\.Tests\.Draft\./Assembly.Tests.Pinned./g' "$to" && rm "$to.bak" && git add "$to" ;; esac
done
echo "pinned $(wc -w <<<"$files" | tr -d ' ') file(s) for $feature; commit them on their own, naming the behavior list: $list"
