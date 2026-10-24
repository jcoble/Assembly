#!/bin/sh
exec node "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/bridge.mjs" "$@"
