#!/usr/bin/env bash
# One way to add a route (AGENTS.md, Backend conventions): a feature's calls go through
# dotnet/Assembly.Engine/Features/FeatureCalls.cs. Fails, naming each line, if any other backend file maps a route.
# The files below map chat's REST routes, the socket and the Mac's page and debug calls, which came before it.
cd "$(dirname "$0")/../.."
allowed='dotnet/Assembly.Engine/Features/FeatureCalls.cs
dotnet/Assembly.Engine/Api/ApiServer.cs
dotnet/Assembly.Engine/Api/AttachmentRoutes.cs
dotnet/Assembly.Engine/Api/ConfigRoutes.cs
dotnet/Assembly.Engine/Api/InfoRoutes.cs
dotnet/Assembly.Engine/Api/McpRoutes.cs
dotnet/Assembly.Engine/Api/OutboxRoutes.cs
dotnet/Assembly.Engine/Api/RowRoutes.cs
dotnet/Assembly.Engine/Api/RunRoutes.cs
dotnet/Assembly.Engine/Api/ThreadRoutes.cs
dotnet/Assembly.Mac/Debug/DebugRoutes.cs
dotnet/Assembly.Mac/Page/PageRoutes.cs'
all=$(git grep -nE '\.Map(Get|Post|Put|Delete|Patch|Methods|Fallback|Group)\(|\.Map\("/' -- 'dotnet/Assembly.Engine/*.cs' 'dotnet/Assembly.Mac/*.cs' 'dotnet/Assembly.Remote/*.cs')
failed=0
while IFS= read -r hit; do
  [ -n "$hit" ] && ! grep -qxF -- "${hit%%:*}" <<<"$allowed" || continue
  echo "routes: $hit maps a route by hand; add it with FeatureCalls.Map" >&2
  failed=1
done <<<"$all"
# The list only shrinks: a listed file that no longer maps a route must come off it.
while IFS= read -r file; do
  grep -q "^$file:" <<<"$all" && continue
  echo "routes: $file is on this script's list but maps no route; take it off" >&2
  failed=1
done <<<"$allowed"
[ "$failed" = 0 ] && echo "routes: every feature route goes through FeatureCalls"
exit "$failed"
