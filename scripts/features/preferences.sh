#!/usr/bin/env bash
# Preferences: a saved value comes back, a later save keeps the keys it doesn't name, and an unknown call is a 404.
set -euo pipefail
call() { # call METHOD JSON -> the answer's body, then its status on the last line
  printf 'Authorization: Bearer %s\nContent-Type: application/json\n' "$CHECK4_TOKEN" \
    | curl -s --max-time 10 -H @- -w '\n%{http_code}' -d "$2" "$CHECK4_API/preferences/$1"
}

mark="check4-$CHECK4_AGENT-$CHECK4_MACHINE-$$"
call set "{\"values\":{\"check4Mark\":\"$mark\",\"check4Other\":\"kept\"}}" >/dev/null
call set "{\"values\":{\"check4Mark\":\"$mark-2\"}}" >/dev/null
got=$(call get '{}' | sed '$d')
if [ "$(jq -r '.result.check4Mark' <<<"$got")" != "$mark-2" ]; then
  echo "fail a saved value comes back: got $(jq -c '.result.check4Mark' <<<"$got")"
  exit 1
fi
echo "ok a saved value comes back"
if [ "$(jq -r '.result.check4Other' <<<"$got")" != kept ]; then
  echo "fail a later save keeps the keys it doesn't name: got $(jq -c '.result.check4Other' <<<"$got")"
  exit 1
fi
echo "ok a later save keeps the keys it doesn't name"
status=$(call nope '{}' | tail -1)
if [ "$status" != 404 ]; then
  echo "fail an unknown call is a 404: got $status"
  exit 1
fi
echo "ok an unknown call is a 404"
