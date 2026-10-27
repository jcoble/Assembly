#!/usr/bin/env bash
# Every query names its columns (plan 5.6): rows are read by column position, so a SELECT * would read the wrong
# column once a table changes. Fails, naming each line, on SELECT * FROM anywhere under dotnet/. A comment that only
# says "SELECT *" isn't a query, so it passes.
cd "$(dirname "$0")/../.."
hits=$(git grep -niE 'SELECT[[:space:]]+\*[[:space:]]+FROM' -- dotnet)
rc=$?
[ "$rc" -le 1 ] || { echo "no select star: git grep failed ($rc); refusing" >&2; exit 2; }
[ -z "$hits" ] && { echo "no select star: every query names its columns"; exit 0; }
while IFS= read -r hit; do echo "no select star: $hit reads SELECT *; name the columns" >&2; done <<<"$hits"
exit 1
