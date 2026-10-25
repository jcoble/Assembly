#!/usr/bin/env bash
# Rows: the live socket sends every row the store keeps, each saying which row came before it. Seqs may skip, since
# a message's deltas are deleted once the message is saved (RowStore.cs), but the prevSeq chain never breaks.
set -euo pipefail
api() { printf 'Authorization: Bearer %s\n' "$CHECK4_TOKEN" | curl -s --max-time 10 -H @- "$CHECK4_API$1"; }
# live AFTER: the rows the socket sends from AFTER within 3 s, as one JSON array (live-rows.ts).
live() { node "$(dirname "$0")/live-rows.ts" "$1"; }

# After the turn ends, so its message has replaced its deltas and the seqs skip.
ended() { jq -e --arg r "$CHECK4_RUN" 'any(.rows[]; .runId == $r and .kind == "turn" and .payload.state != "started")' <<<"$page" >/dev/null; }
for _ in $(seq 90); do
  page=$(api "/threads/$CHECK4_THREAD/rows?limit=500")
  ended && break
  sleep 1
done
if ! ended; then
  echo "fail the turn ended within 90 s: it's still running"
  exit 1
fi
newest=$(jq '[.rows[].seq] | max // 0' <<<"$page")
rows=$(live 0)
count=$(jq length <<<"$rows")
if [ "$count" -eq 0 ]; then
  echo "fail the socket from 0 sent rows: it sent none"
  exit 1
fi
echo "ok the socket from 0 sent $count rows"

broken=$(jq -r 'to_entries[] | select(.value.prevSeq != (if .key == 0 then 0 else $rows[.key - 1].seq end)) | "\(.value.seq) says prevSeq \(.value.prevSeq)"' \
  --argjson rows "$rows" <<<"$rows" | head -1)
backwards=$(jq -r '[.[].seq] as $s | [range(1; $s | length) | select($s[.] <= $s[. - 1]) | $s[.]] | first // empty' <<<"$rows")
if [ -n "$broken" ]; then
  echo "fail every row's prevSeq is the row before it: seq $broken"
  exit 1
elif [ -n "$backwards" ]; then
  echo "fail seqs only rise: seq $backwards repeats or goes back"
  exit 1
fi
skipped=$(jq '[.[] | select(.seq != .prevSeq + 1)] | length' <<<"$rows")
echo "ok every row's prevSeq is the row before it, and seqs only rise ($skipped skip past deleted deltas)"

last=$(jq '.[-1].seq' <<<"$rows")
if [ "$last" -lt "$newest" ]; then
  echo "fail the socket reaches the newest row: it stopped at $last, the rows page has $newest"
  exit 1
fi
echo "ok the socket reaches the newest row ($last)"

if [ "$count" -ge 2 ]; then
  middle=$(jq ".[$((count / 2 - 1))].seq" <<<"$rows")
  expected=$(jq ".[$((count / 2))].seq" <<<"$rows")
  first=$(live "$middle" | jq -c '.[0] // {}')
  if [ "$(jq '.prevSeq' <<<"$first")" != "$middle" ] || [ "$(jq '.seq' <<<"$first")" != "$expected" ]; then
    echo "fail the socket from $middle starts at $expected with prevSeq $middle: it started with $first"
    exit 1
  fi
  echo "ok the socket from $middle starts at $expected with prevSeq $middle"
fi
