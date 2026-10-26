#!/usr/bin/env bash
# The core is in layers (AGENTS.md, One way to do each thing): fails if dotnet/Assembly.Engine/, which B2c split into
# Domain, Application and Infrastructure, comes back.
cd "$(dirname "$0")/../.."
[ -z "$(git ls-files -- dotnet/Assembly.Engine)" ] && { echo "no engine: dotnet/Assembly.Engine/ stays gone"; exit 0; }
echo "no engine: dotnet/Assembly.Engine/ is back; its code belongs in Domain, Application or Infrastructure" >&2
exit 1
