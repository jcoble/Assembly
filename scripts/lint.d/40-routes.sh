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
hits=$(git grep -nE '\.Map(Get|Post|Put|Delete|Patch|Methods|Fallback|Group)\(|\.Map\("/' -- 'dotnet/Assembly.Engine/*.cs' 'dotnet/Assembly.Mac/*.cs' 'dotnet/Assembly.Remote/*.cs' \
  | while IFS= read -r hit; do grep -qxF -- "${hit%%:*}" <<<"$allowed" || echo "$hit"; done)
[ -z "$hits" ] && { echo "routes: every feature route goes through FeatureCalls"; exit 0; }
while IFS= read -r hit; do echo "routes: $hit maps a route by hand; add it with FeatureCalls.Map" >&2; done <<<"$hits"
exit 1
