#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
fixture="$tmp/repo"
mkdir -p "$fixture/scripts/lint.d" "$fixture/dotnet/Assembly.Infrastructure/Http" "$fixture/dotnet/Assembly.Infrastructure/Mcp" "$fixture/dotnet/Assembly.Domain"
cp "$repo_root/scripts/lint.d/40-routes.sh" "$fixture/scripts/lint.d/40-routes.sh"
cd "$fixture"
git init -q
git config user.email route-guard@example.invalid
git config user.name 'Route Guard Test'

# These are the retained shared host route owners. Each stub exercises the real guard's route mapping scan.
for file in \
  dotnet/Assembly.Infrastructure/Http/ApiServer.cs \
  dotnet/Assembly.Infrastructure/Http/AttachmentRoutes.cs \
  dotnet/Assembly.Infrastructure/Http/ConfigRoutes.cs \
  dotnet/Assembly.Infrastructure/Http/InfoRoutes.cs \
  dotnet/Assembly.Infrastructure/Http/OutboxRoutes.cs \
  dotnet/Assembly.Infrastructure/Http/RowRoutes.cs \
  dotnet/Assembly.Infrastructure/Http/RunRoutes.cs \
  dotnet/Assembly.Infrastructure/Http/SessionRoutes.cs \
  dotnet/Assembly.Infrastructure/Mcp/McpRoutes.cs; do
  mkdir -p "$(dirname "$file")"
  printf 'app.Map("/fixture", handler);\n' > "$file"
done
git add -A
git commit -qm 'fixture baseline'

if ! output=$(bash scripts/lint.d/40-routes.sh 2>&1); then
  printf 'expected retained shared route stubs to pass, got:\n%s\n' "$output" >&2
  exit 1
fi

expect_rejected() {
  local expected="$1"
  if output=$(bash scripts/lint.d/40-routes.sh 2>&1); then
    printf 'expected guard rejection containing %s\n' "$expected" >&2
    exit 1
  fi
  if [[ "$output" != *"$expected"* ]]; then
    printf 'expected rejection containing %s, got:\n%s\n' "$expected" "$output" >&2
    exit 1
  fi
}

# Feature route mapping must fail regardless of whether it is named like the retired adapter.
printf 'app.Map("/feature", handler);\n' > dotnet/Assembly.Domain/FeatureRoute.cs
git add -A
expect_rejected 'FeatureRoute.cs:1'
rm dotnet/Assembly.Domain/FeatureRoute.cs

# Reintroducing the removed adapter as a route owner is rejected too.
printf 'app.Map("/feature", handler);\n' > dotnet/Assembly.Infrastructure/Http/FeatureCalls.cs
git add -A
expect_rejected 'FeatureCalls.cs:1'
rm dotnet/Assembly.Infrastructure/Http/FeatureCalls.cs

# An allowlisted owner with no remaining route is reported as stale.
rm dotnet/Assembly.Infrastructure/Http/AttachmentRoutes.cs
git add -A
expect_rejected 'AttachmentRoutes.cs is on this script'
printf 'routes: retained route guard behavior passed\n'
