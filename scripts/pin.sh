#!/usr/bin/env bash
# Moves a feature's draft tests into the pinned folders once Jesse has corrected its behavior list:
#   scripts/pin.sh <feature>   dotnet/Assembly.Tests/Draft/<Feature>/ -> Pinned/<Feature>/ (namespace too), and
#                              app/test/draft/<feature>/ -> app/test/pinned/<feature>/, staged for a commit of their own
# It refuses while docs/behavior/<feature>.md still says it's a draft, if a test doesn't name the list line it pins
# (// Pins: <feature>#<n>), or if a pinned file of the same name exists.
set -euo pipefail
cd "$(dirname "$0")/.."
feature=${1:?usage: scripts/pin.sh <feature>}
list=docs/behavior/$feature.md
[ -f "$list" ] || { echo "no behavior list $list" >&2; exit 1; }
if grep -q "Draft by Claude" "$list" || ! grep -q "^Corrected by Jesse on " "$list"; then
  echo "$list is still a draft; tests are pinned only after Jesse corrects it" >&2; exit 1
fi

dirs=()
cs=$(find dotnet/Assembly.Tests/Draft -mindepth 1 -maxdepth 1 -type d -iname "$feature" 2>/dev/null | head -1)
[ -n "$cs" ] && dirs+=("$cs")
[ -d "app/test/draft/$feature" ] && dirs+=("app/test/draft/$feature")
[ ${#dirs[@]} -gt 0 ] || { echo "no draft tests for $feature" >&2; exit 1; }

files=$(git ls-files -- "${dirs[@]}")
[ -n "$files" ] || { echo "no committed draft tests for $feature" >&2; exit 1; }
failed=0
for f in $files; do
  case "$f" in *.cs) grep -qE '^\s*\[(Fact|Theory|MacFact)\b' "$f" || continue ;; *.test.ts) ;; *) continue ;; esac
  grep -qE "Pins: $feature#[0-9]+" "$f" || { echo "$f doesn't name the line it pins (// Pins: $feature#<n>)" >&2; failed=1; }
done
for f in $files; do
  to=$(sed -E 's#^dotnet/Assembly\.Tests/Draft/#dotnet/Assembly.Tests/Pinned/#; s#^app/test/draft/#app/test/pinned/#' <<<"$f")
  [ -e "$to" ] && { echo "$to already exists" >&2; failed=1; }
done
[ "$failed" = 0 ] || exit 1

for f in $files; do
  to=$(sed -E 's#^dotnet/Assembly\.Tests/Draft/#dotnet/Assembly.Tests/Pinned/#; s#^app/test/draft/#app/test/pinned/#' <<<"$f")
  mkdir -p "$(dirname "$to")"
  git mv "$f" "$to"
  case "$to" in *.cs) sed -i.bak 's/Assembly\.Tests\.Draft\./Assembly.Tests.Pinned./g' "$to" && rm "$to.bak" && git add "$to" ;; esac
done
echo "pinned $(wc -w <<<"$files" | tr -d ' ') file(s) for $feature; commit them on their own"
