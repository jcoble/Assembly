#!/usr/bin/env bash
# Provider quirks stay where the adapters' updates are read (AGENTS.md, Providers behave the same): fails, naming each
# line, if code outside dotnet/Assembly.Application/Common/Acp/V1/ branches on an agent's name (claude, codex,
# antigravity). The matcher is mac-command-bar's (commitChecks.ts), in POSIX regex; tests are not scanned.
cd "$(dirname "$0")/../.."
w='[A-Za-z0-9_]'
name="(['\"](codex|claude|antigravity)['\"]|($w+::)+(Codex|Claude|Antigravity))"
branch="[!=]==?[[:space:]]*$name|$name[[:space:]]*[!=]==?|(^|[^A-Za-z0-9_])case[[:space:]]+$name[[:space:]]*:|$name[^=]*=>|(^|[^A-Za-z0-9_])matches![[:space:]]*\(.*$name"
hits=$(git grep -nE "$branch" -- '*.cs' '*.ts' '*.svelte' '*.rs' \
  ':!dotnet/Assembly.Application/Common/Acp/V1/' ':!dotnet/Assembly.Tests/' ':!app/test/' ':!*.test.ts')
rc=$?
[ "$rc" -le 1 ] || { echo "provider branches: git grep failed ($rc); refusing" >&2; exit 2; }
[ -z "$hits" ] && { echo "provider branches: none outside Acp/V1"; exit 0; }
while IFS= read -r hit; do
  echo "provider branches: $hit branches on an agent's name; that belongs in dotnet/Assembly.Application/Common/Acp/V1/" >&2
done <<<"$hits"
exit 1
