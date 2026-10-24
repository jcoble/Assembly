#!/usr/bin/env bash
# Runs only the pinned tests and fails if any fails, if any is skipped other than a [MacFact] off macOS, or if fewer
# run than the pinned files declare ([Fact]/[Theory]/[MacFact] attributes; test( calls). That catches a pinned test
# that stops running: a <Compile Remove>, a Skip, a namespace outside Assembly.Tests.Pinned, a glob that misses it.
# Runs on every push to main (.github/workflows/pinned.yml) and locally. Needs app/'s packages installed for app tests.
set -euo pipefail
cd "$(dirname "$0")/.."
failed=0
fail() { echo "pinned tests: $*" >&2; failed=1; }

cs=$(find dotnet/Assembly.Tests/Pinned -name '*.cs' 2>/dev/null)
if [ -n "$cs" ]; then
  declared=$(cat $cs | grep -cE '^\s*\[(Fact|Theory|MacFact)\b' || true)
  macfacts=$(cat $cs | grep -cE '^\s*\[MacFact\b' || true)
  expected_skips=$macfacts; [ "$(uname)" = Darwin ] && expected_skips=0
  out=$(MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    dotnet test dotnet/Assembly.Tests --nologo -v q --filter 'FullyQualifiedName~Assembly.Tests.Pinned.' 2>&1 || true)
  line=$(grep -E '(Passed|Failed)! +- Failed: ' <<<"$out" | tail -1 || true)
  num() { sed -nE "s/.*$1: *([0-9]+).*/\1/p" <<<"$line"; }
  f=$(num Failed) p=$(num Passed) s=$(num Skipped) t=$(num Total)
  : "${f:=0}" "${p:=0}" "${s:=0}" "${t:=0}"
  echo ".NET pinned: $p passed, $f failed, $s skipped of $t run; $declared declared ($macfacts MacFact)"
  [ -n "$line" ] || { grep -iE 'error|no test matches' <<<"$out" | tail -5 >&2 || true; fail "no .NET pinned test ran of $declared declared"; }
  [ "$f" = 0 ] || { grep -E '\[FAIL\]' <<<"$out" >&2; fail "$f .NET pinned test(s) failed"; }
  [ "$s" = "$expected_skips" ] || fail "$s .NET pinned test(s) skipped, expected $expected_skips (only MacFact off macOS may skip)"
  [ "$t" -ge "$declared" ] || fail "only $t .NET pinned test(s) ran of $declared declared"
fi

ts=$(find app/test/pinned -name '*.test.ts' 2>/dev/null)
if [ -n "$ts" ]; then
  declared=$(cat $ts | grep -cE '(^|[^.a-zA-Z_])test(\.[a-z]+)?\(' || true)
  out=$(cd app && node --test 'test/pinned/**/*.test.ts' 2>&1 || true)
  num() { sed -nE "s/^ℹ $1 ([0-9]+)$/\1/p" <<<"$out" | tail -1; }
  t=$(num tests) p=$(num pass) f=$(num fail) s=$(num skipped) d=$(num todo)
  : "${t:=0}" "${p:=0}" "${f:=0}" "${s:=0}" "${d:=0}"
  echo "app pinned: $p passed, $f failed, $s skipped, $d todo of $t run; $declared declared"
  [ "$f" = 0 ] || { grep -E '^not ok|✖' <<<"$out" >&2; fail "$f app pinned test(s) failed"; }
  [ "$s" = 0 ] && [ "$d" = 0 ] || fail "app pinned tests may not be skipped or todo"
  [ "$t" -ge "$declared" ] || fail "only $t app pinned test(s) ran of $declared declared"
fi

[ -n "$cs$ts" ] || echo "no pinned tests yet"
[ "$failed" = 0 ] && echo "pinned tests: all ran and passed"
exit "$failed"
