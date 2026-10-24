# Feature scenarios

A feature's scenario proves the feature works, not only that a turn runs. `scripts/check4.sh` runs every
`scripts/features/<name>.sh` in each of the four cases (Claude Code and Codex, on the local backend and on the
lane's Workbox Test host), while that case's turn streams. A case passes only if its turn and every scenario pass.

The lane writes the scenario against the API, from the feature's corrected spec. It's called with:

- `CHECK4_API`: the local backend, for example `http://127.0.0.1:18797`
- `CHECK4_TOKEN`: its bearer token. Send it on curl's stdin as below; never print it or put it in argv
- `CHECK4_THREAD`, `CHECK4_RUN`: the case's thread and the turn running in it
- `CHECK4_AGENT`: `claude` or `codex`; `CHECK4_MACHINE`: `local` or `workbox-test`
- `CHECK4_REPO`: the thread's repo, on the machine it runs on

It prints one line per step, `ok <step>` or `fail <step>: <why>`, and exits 0 only if every step passed. The lines
are saved as the case's steps in the summary.

A tiny example, `scripts/features/threads.sh`, checking that the thread runs where it should:

```bash
#!/usr/bin/env bash
set -euo pipefail
api() { printf 'Authorization: Bearer %s\n' "$CHECK4_TOKEN" | curl -s --max-time 10 -H @- "$CHECK4_API$1"; }

machine=$(api "/threads/$CHECK4_THREAD" | jq -r '.machine // "local"')
if [ "$machine" = "$CHECK4_MACHINE" ]; then
  echo "ok the thread runs on $machine"
else
  echo "fail the thread runs on $machine: expected $CHECK4_MACHINE"
  exit 1
fi
```

Every check runs every scenario, so a lane that breaks a feature already landed finds out. Keep a scenario short,
seconds rather than minutes, so a full run stays cheap. `scripts/check4.sh --feature <name>` runs only that one, for
quick runs while working; its summary goes to `four-way-partial/` and isn't evidence.
