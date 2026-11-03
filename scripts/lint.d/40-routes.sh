#!/usr/bin/env bash
# Register feature calls with IFeature and ICallRegistry; see Assembly's backend:
# https://app.notion.com/p/40a394b0689d81ffb636c44de6144420. This guard rejects hand-mapped feature routes.
# The allowlist is limited to shared host routes; each listed file must still map a route.
cd "$(dirname "$0")/../.."
allowed='dotnet/Assembly.Infrastructure/Http/ApiServer.cs
dotnet/Assembly.Infrastructure/Mcp/McpServerHost.cs'
all=$(git grep -nE '\.Map(Get|Post|Put|Delete|Patch|Methods|Fallback|Group|Mcp)\(|\.Map\("/' -- 'dotnet/Assembly.Domain/*.cs' 'dotnet/Assembly.Application/*.cs' 'dotnet/Assembly.Infrastructure/*.cs' 'dotnet/Assembly.Desktop/*.cs' 'dotnet/Assembly.Remote/*.cs')
failed=0
while IFS= read -r hit; do
  [ -n "$hit" ] && ! grep -qxF -- "${hit%%:*}" <<<"$allowed" || continue
  echo "routes: $hit maps a route by hand; register feature calls with IFeature/ICallRegistry" >&2
  failed=1
done <<<"$all"
# The list only shrinks: a listed file that no longer maps a route must come off it.
while IFS= read -r file; do
  grep -q "^$file:" <<<"$all" && continue
  echo "routes: $file is on this script's list but maps no route; take it off" >&2
  failed=1
done <<<"$allowed"
[ "$failed" = 0 ] && echo "routes: feature calls use IFeature/ICallRegistry"
exit "$failed"
