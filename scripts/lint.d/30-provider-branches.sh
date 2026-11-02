#!/usr/bin/env bash
# Provider behavior stays consistent (AGENTS.md, Providers behave the same): fails, naming each line, if code
# branches on an agent ID/name (claude, codex, antigravity). Only the exact provider icon mapping and explicit
# VendorFamily/account-home interpretation boundaries below are exempt. The matcher is mac-command-bar's
# (commitChecks.ts), in POSIX regex; tests are not scanned.
cd "$(dirname "$0")/../.."
w='[A-Za-z0-9_]'
name="(['\"](codex|claude|antigravity)['\"]|($w+::)+(Codex|Claude|Antigravity))"
branch="[!=]==?[[:space:]]*$name|$name[[:space:]]*[!=]==?|(^|[^A-Za-z0-9_])case[[:space:]]+$name[[:space:]]*:|$name[^=]*=>|(^|[^A-Za-z0-9_])matches![[:space:]]*\(.*$name"
hits=$(git grep -nE "$branch" -- '*.cs' '*.ts' '*.svelte' '*.rs' \
  ':!dotnet/Assembly.Application/Common/Acp/ProviderGlyph.cs' ':!dotnet/Assembly.Application/Common/Agents/AccountContext.cs' ':!dotnet/Assembly.Tests/' ':!app/test/' ':!*.test.ts')
rc=$?
[ "$rc" -le 1 ] || { echo "provider branches: git grep failed ($rc); refusing" >&2; exit 2; }
[ -z "$hits" ] && { echo "provider branches: none outside the exact icon and VendorFamily/account-home boundaries"; exit 0; }
while IFS= read -r hit; do
  echo "provider branches: $hit branches on an agent ID/name outside ProviderGlyph.cs or the exact AccountContext.cs VendorFamily/account-home boundary" >&2
done <<<"$hits"
exit 1
