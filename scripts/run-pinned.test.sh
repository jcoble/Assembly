#!/usr/bin/env bash
# Exercises the pinned runner's .NET result handling with a fake dotnet command and declared scratch tests.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
source_root=$(cd "$here/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
repo="$tmp/repo"
mkdir -p "$repo/scripts" "$repo/dotnet/Assembly.Tests/Pinned" "$tmp/bin"
ln -s "$source_root/scripts/run-pinned.sh" "$repo/scripts/run-pinned.sh"
cat >"$repo/dotnet/Assembly.Tests/Pinned/Fixture.cs" <<'EOF'
[Fact]
[Theory]
[Fact]
[MacFact]
EOF
cat >"$tmp/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
case "$PINNED_CASE" in
  missing)
    echo "No test matches the given testcase filter"
    ;;
  pass|fail|extra-skip|underrun)
    skips=0
    [ "$(uname)" = Darwin ] || skips=1
    failures=0
    total=4
    case "$PINNED_CASE" in
      fail) failures=1 ;;
      extra-skip) skips=$((skips + 1)) ;;
      underrun) total=3 ;;
    esac
    passed=$((total - skips - failures))
    printf 'Passed! - Failed: %s, Passed: %s, Skipped: %s, Total: %s\n' "$failures" "$passed" "$skips" "$total"
    ;;
  *) exit 2 ;;
esac
EOF
chmod +x "$tmp/bin/dotnet"

failed=0
expect() {
  local want="$1" name="$2" scenario="$3" output got
  output=$(PATH="$tmp/bin:$PATH" PINNED_CASE="$scenario" "$repo/scripts/run-pinned.sh" 2>&1)
  got=$?
  if { [ "$want" = pass ] && [ "$got" -eq 0 ]; } || { [ "$want" = fail ] && [ "$got" -ne 0 ]; }; then
    echo "ok   $name"
  else
    echo "FAIL $name (exit $got): $output"
    failed=1
  fi
}

expect fail "missing .NET test result is rejected" missing
expect fail "failing .NET test is rejected" fail
expect fail "unexpected .NET skip beyond MacFact allowance is rejected" extra-skip
expect fail "fewer .NET tests than declared is rejected" underrun
expect pass "retained counts pass with only the platform's MacFact skip allowance" pass
exit "$failed"
