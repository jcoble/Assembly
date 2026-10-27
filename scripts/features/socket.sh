#!/usr/bin/env bash
# The socket: hello, a welcome from the case's own machine, and a not_found answer (socket.ts).
exec node "$(dirname "$0")/socket.ts"
