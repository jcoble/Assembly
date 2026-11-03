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
owners=(
  dotnet/Assembly.Infrastructure/Http/ApiServer.cs
  dotnet/Assembly.Infrastructure/Http/AttachmentRoutes.cs
  dotnet/Assembly.Infrastructure/Http/OutboxRoutes.cs
  dotnet/Assembly.Infrastructure/Http/RowRoutes.cs
  dotnet/Assembly.Infrastructure/Mcp/McpServerHost.cs
)
write_owner() {
  if [ "$1" = dotnet/Assembly.Infrastructure/Mcp/McpServerHost.cs ]; then
    printf 'app.MapMcp("/mcp");\n' > "$1"
  else
    printf 'app.Map("/fixture", handler);\n' > "$1"
  fi
}
for file in "${owners[@]}"; do
  mkdir -p "$(dirname "$file")"
  write_owner "$file"
done
git add -A
git commit -qm 'fixture baseline'

if ! output=$(bash scripts/lint.d/40-routes.sh 2>&1); then
  printf 'expected retained shared route stubs to pass, got:\n%s\n' "$output" >&2
  exit 1
fi
printf 'ok exact retained route owners pass\n'

rejected=0
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
  rejected=$((rejected + 1))
  printf 'ok guard rejected %s\n' "$expected"
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

# Exact infrastructure file allowances never exempt a neighboring file or a whole phone/socket folder.
for file in \
  dotnet/Assembly.Infrastructure/Http/ApiServerExtra.cs \
  dotnet/Assembly.Infrastructure/Mcp/OtherMcpRoutes.cs \
  dotnet/Assembly.Infrastructure/Socket/PhoneRoutes.cs \
  dotnet/Assembly.Application/Features/Preferences/PreferencesRoutes.cs \
  dotnet/Assembly.Desktop/Serve/FeatureRoutes.cs \
  dotnet/Assembly.Remote/Serve/FeatureRoutes.cs; do
  mkdir -p "$(dirname "$file")"
  for mapping in MapPost MapMcp; do
    printf 'app.%s("/feature", handler);\n' "$mapping" > "$file"
    git add -A
    expect_rejected "$file:1"
    rm "$file"
    git add -A
  done
done

# Every scanned mapping form must fail outside the actual infrastructure owners.
for mapping in MapGet MapPost MapPut MapDelete MapPatch MapMethods MapFallback MapGroup Map MapMcp; do
  file=dotnet/Assembly.Domain/FeatureRoute.cs
  printf 'app.%s("/feature", handler);\n' "$mapping" > "$file"
  git add -A
  expect_rejected 'FeatureRoute.cs:1'
  rm "$file"
  git add -A
done

# Retired exact owners cannot return as another hand-mapped feature path in any supported mapping form.
for file in \
  dotnet/Assembly.Infrastructure/Http/ConfigRoutes.cs \
  dotnet/Assembly.Infrastructure/Http/InfoRoutes.cs \
  dotnet/Assembly.Infrastructure/Http/SessionRoutes.cs \
  dotnet/Assembly.Infrastructure/Http/RunRoutes.cs \
  dotnet/Assembly.Infrastructure/Mcp/McpRoutes.cs; do
  for mapping in MapGet MapPost MapPut MapDelete MapPatch MapMethods MapFallback MapGroup Map MapMcp; do
    printf 'app.%s("/retired-feature", handler);\n' "$mapping" > "$file"
    git add -A
    expect_rejected "$file:1"
    rm "$file"
    git add -A
  done
done

# Each allowlisted owner with no remaining route is stale; deleting the file or its last mapping cannot hide it.
for file in "${owners[@]}"; do
  printf '// no route remains\n' > "$file"
  git add -A
  expect_rejected "$file is on this script"
  rm "$file"
  git add -A
  expect_rejected "$file is on this script"
  write_owner "$file"
  git add -A
done

if ! output=$(bash scripts/lint.d/40-routes.sh 2>&1); then
  printf 'expected restored exact route owners to pass, got:\n%s\n' "$output" >&2
  exit 1
fi
printf 'routes: %s rejected invalid fixtures; both retained-owner baselines passed\n' "$rejected"
