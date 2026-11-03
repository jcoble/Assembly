#!/usr/bin/env bash
# Register feature calls with IFeature and ICallRegistry; see Assembly's backend:
# https://app.notion.com/p/40a394b0689d81ffb636c44de6144420. This guard rejects hand-mapped feature routes.
# The retained /phone/v1 compatibility API is an owner-approved exception (P4 Tasks11/12).
# Exact shared host owners and present phone adapters must still map a route.
cd "$(dirname "$0")/../.."
allowed='dotnet/Assembly.Infrastructure/Http/ApiServer.cs
dotnet/Assembly.Infrastructure/Mcp/McpServerHost.cs'
phone='dotnet/Assembly.Infrastructure/Phone/PhoneChatRoutes.cs
dotnet/Assembly.Infrastructure/Phone/PhoneNeedsRoutes.cs
dotnet/Assembly.Infrastructure/Phone/PhonePairRoutes.cs
dotnet/Assembly.Infrastructure/Phone/PhoneDeviceRoutes.cs
dotnet/Assembly.Infrastructure/Phone/PhoneLiveRoutes.cs'
mapping='\.Map(Get|Post|Put|Delete|Patch|Methods|Fallback|Group|Mcp)?\('
phone_literal='^\.Map(Get|Post|Put|Delete|Patch|Methods|Fallback|Group)?\("/phone/v1/[^"[:space:]]*"[[:space:]]*$'
mapfile -t phone_files <<<"$phone"
all=$({
  git grep -nE '\.Map(Get|Post|Put|Delete|Patch|Methods|Fallback|Group|Mcp)\(|\.Map\("/' -- 'dotnet/Assembly.Domain/*.cs' 'dotnet/Assembly.Application/*.cs' 'dotnet/Assembly.Infrastructure/*.cs' 'dotnet/Assembly.Desktop/*.cs' 'dotnet/Assembly.Remote/*.cs'
  # Also reject dynamic Map(path, ...) in a phone exception, without mistaking host composition Map(app) for a route.
  git grep -nE "$mapping" -- "${phone_files[@]}"
} | sort -u)
failed=0
while IFS= read -r hit; do
  [ -n "$hit" ] && ! grep -qxF -- "${hit%%:*}" <<<"$allowed" || continue
  if grep -qxF -- "${hit%%:*}" <<<"$phone"; then
    # Inspect every mapping on the line; a phone mapping cannot hide an adjacent owner route.
    invalid=0
    while IFS= read -r call; do
      [[ "$call" =~ $phone_literal ]] || invalid=1
    done < <(grep -oE "$mapping[^,)]*" <<<"${hit#*:*:}")
    [ "$invalid" = 0 ] && continue
  fi
  echo "routes: $hit maps a route by hand; register feature calls with IFeature/ICallRegistry" >&2
  failed=1
done <<<"$all"
# Retired owner routes stay retired; the closed phone exceptions never allow an owner HTTP path.
while IFS= read -r file; do
  grep -q "^$file:" <<<"$all" && continue
  echo "routes: $file is on this script's list but maps no route; take it off" >&2
  failed=1
done <<<"$allowed"
while IFS= read -r file; do
  git ls-files --error-unmatch "$file" >/dev/null 2>&1 || continue
  grep -q "^$file:" <<<"$all" && continue
  echo "routes: $file is on this script's list but maps no route; take it off" >&2
  failed=1
done <<<"$phone"
[ "$failed" = 0 ] && echo "routes: feature calls use IFeature/ICallRegistry"
exit "$failed"
