#!/usr/bin/env bash
# Tests wait on events, never on time: fails, naming each line, if a tracked .cs file under dotnet/Assembly.Tests
# calls Task.Delay, Thread.Sleep or SpinUntil, or has a method or call named Until or UntilAsync. A fake that hangs
# until it's cancelled, Task.Delay(Timeout.Infinite, ...), passes.
cd "$(dirname "$0")/../.."
polling='Task\.Delay\(|Thread\.Sleep\(|SpinUntil\(|(^|[^A-Za-z0-9_])Until(Async)?[[:space:]]*\('
hits=$(git grep -nE "$polling" -- 'dotnet/Assembly.Tests/*.cs')
rc=$?
[ "$rc" -le 1 ] || { echo "polling in tests: git grep failed ($rc); refusing" >&2; exit 2; }
hits=$(printf '%s' "$hits" | grep -vF 'Task.Delay(Timeout.Infinite')
[ -z "$hits" ] && { echo "polling in tests: none"; exit 0; }
while IFS= read -r hit; do
  echo "polling in tests: $hit waits on time; wait on the event that marks it done, or time out a wait on what mustn't happen" >&2
done <<<"$hits"
exit 1
