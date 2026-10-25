#!/usr/bin/env bash
# Preferences: a saved value comes back, a later save keeps the keys it doesn't name, and an unknown call is a 404.
# The Mac serves preferences and Workbox Test doesn't, so a Workbox Test case only says so. It uses two fixed keys and
# puts back what they held (null when they held nothing, since there's no delete call).
set -euo pipefail
if [ "$CHECK4_MACHINE" != local ]; then
  echo "ok skipped on $CHECK4_MACHINE: Mac-only"
  exit 0
fi
call() { # call METHOD JSON -> the answer's body, then its status on the last line
  printf 'Authorization: Bearer %s\nContent-Type: application/json\n' "$CHECK4_TOKEN" \
    | curl -s --max-time 10 -H @- -w '\n%{http_code}' -d "$2" "$CHECK4_API/preferences/$1"
}

before=$(call get '{}' | sed '$d' | jq -c '{values: {check4a: (.result.check4a // null), check4b: (.result.check4b // null)}}')
trap 'call set "$before" >/dev/null' EXIT
mark="$CHECK4_AGENT-$$"
call set "{\"values\":{\"check4a\":\"$mark\",\"check4b\":\"kept\"}}" >/dev/null
call set "{\"values\":{\"check4a\":\"$mark-2\"}}" >/dev/null
got=$(call get '{}' | sed '$d')
if [ "$(jq -r '.result.check4a' <<<"$got")" != "$mark-2" ]; then
  echo "fail a saved value comes back: got $(jq -c '.result.check4a' <<<"$got")"
  exit 1
fi
echo "ok a saved value comes back"
if [ "$(jq -r '.result.check4b' <<<"$got")" != kept ]; then
  echo "fail a later save keeps the keys it doesn't name: got $(jq -c '.result.check4b' <<<"$got")"
  exit 1
fi
echo "ok a later save keeps the keys it doesn't name"
status=$(call nope '{}' | tail -1)
if [ "$status" != 404 ]; then
  echo "fail an unknown call is a 404: got $status"
  exit 1
fi
echo "ok an unknown call is a 404"
