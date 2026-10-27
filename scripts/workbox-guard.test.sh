#!/usr/bin/env bash
# Tests for the guard in dotnet/scripts/workbox.sh: `serve` for main's host (no .lane) runs only a clean checkout
# of a freshly fetched origin/main, and it prints the lane values it resolved before anything else. Runs it in a throwaway clone whose origin is a local bare repo, with a fake ssh
# on PATH that only notes it was called, and checks the guard stops before any ssh, or lets it through.
# Run: bash scripts/workbox-guard.test.sh
set -uo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin" "$tmp/src/scripts" "$tmp/src/dotnet/scripts"
cat >"$tmp/bin/ssh" <<'SSH'
#!/usr/bin/env bash
echo called >>"$SSH_LOG"
echo "ssh called" >&2
cat >/dev/null
exit 1
SSH
chmod +x "$tmp/bin/ssh"
cp "$here/scripts/lane-env.sh" "$tmp/src/scripts/"
cp "$here/dotnet/scripts/workbox.sh" "$tmp/src/dotnet/scripts/"
git -C "$tmp/src" init -q -b main
git -C "$tmp/src" add -A
git -C "$tmp/src" -c user.name=t -c user.email=t@t commit -q -m base
git clone -q --bare "$tmp/src" "$tmp/origin.git"
git clone -q "$tmp/origin.git" "$tmp/work"
work=$tmp/work
failed=0
# expect <stops|passes> <name>: stops means it exits non-zero with no ssh call; passes means it reached ssh.
expect() {
  rm -f "$tmp/ssh.log"
  (cd "$work" && PATH="$tmp/bin:$PATH" SSH_LOG="$tmp/ssh.log" bash dotnet/scripts/workbox.sh serve) </dev/null >"$tmp/out" 2>&1
  local got=$? called=no
  [ -f "$tmp/ssh.log" ] && called=yes
  if { [ "$1" = stops ] && [ "$got" != 0 ] && [ "$called" = no ]; } || { [ "$1" = passes ] && [ "$called" = yes ]; }
  then echo "ok   $2"; else echo "FAIL $2: exit $got, ssh called: $called, wanted $1"; failed=1; fi
}
expect passes "main's host from a clean checkout of origin/main"
echo dirty >"$work/scratch.txt"
expect stops "main's host from a checkout with uncommitted files"
rm "$work/scratch.txt"
git -C "$work" -c user.name=t -c user.email=t@t commit -q --allow-empty -m ahead
expect stops "main's host from main one commit ahead of origin/main"
git -C "$work" reset -q --soft HEAD~1
git -C "$tmp/src" -c user.name=t -c user.email=t@t commit -q --allow-empty -m newer
git -C "$tmp/src" push -q "$tmp/origin.git" main
expect stops "main's host from a checkout behind a fresh origin/main"
git -C "$work" pull -q --ff-only
git -C "$work" checkout -q -b lane/x
printf 'LANE_NAME=x\nLANE_NUMBER=2\n' >"$work/.lane"
echo .lane >"$work/.git/info/exclude"
git -C "$work" -c user.name=t -c user.email=t@t commit -q --allow-empty -m lane-work
expect passes "a lane's own host from its lane branch"
if head -1 "$tmp/out" | grep -qx "lane x: box port 9817, unit assembly-dotnet-x, box data ~/assembly-dotnet-x"; then
  echo "ok   the resolved lane values come first, before any ssh"
else echo "FAIL the first line isn't the lane values: $(head -1 "$tmp/out")"; failed=1; fi
exit $failed
