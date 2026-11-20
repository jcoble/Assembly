# The verification bench

A journey drives the real native app by accessibility id on the Workbox, local and on Workbox Test
(see [workbox-test.md](workbox-test.md) for that host). A PR verifier works from
[scripts/verify-pr/prompt.md](../../scripts/verify-pr/prompt.md): the smoke there is the journey below, then a hand check in this window.

On the Workbox, from a lane worktree (it has a `.lane` file), with nothing of that lane already running:

```sh
bash scripts/bench.sh up                       # about 1 minute with builds kept; about 2 (113 to 116 s seen) when the
                                               # backend is rebuilt and deployed; 3 to 5 when the desktop rebuilds too
bash scripts/journey.sh chat-steer all         # or local / workbox-test; 4 cases, about 50 s each
bash scripts/bench.sh down                     # always, also after a failure
```

- Each case writes `report.md` (every step, and for a failure what was expected and what the rows showed),
  `result.json`, `video.mp4` and `app-log.txt` under `$DATA/journeys/<utc>-chat-steer/<agent>-<machine>/`, with
  `summary.md` one level up. A failure adds `fail.png`. `$DATA` comes from `scripts/lane-env.sh`.
  - `app-log.txt` is the lane's backend log from the moment the case started, so lines from earlier cases and runs are
    not in it: count `socket lost` or `fell behind` for a case there.
  - The backend log itself is `appLog` in `$DATA/bench/state.json` (`backend.log` in the lane's seeded dev profile,
    `~/assembly-dev/worktrees/<lane>-<hash>/`). Every bench run on the lane appends to it, and its lines have no timestamps.
  - `$DATA/mac/backend.log` is not the bench's: it belongs to the lane profile that `dotnet/scripts/desktop.sh serve`
    and `app:dev` use, so it is stale or empty for a bench run.
  - `fail.png` is the window at the failure. A failure before the session opens (at session creation) happens before the
    window changes, so the picture shows the window as it was (often the previous case's session) with no error; the
    report's failed step has the error.
- `BLOCKED: provider` means the agent couldn't run (a failed login or a usage limit), not that Assembly is wrong; the
  bench checks both logins on both machines during `up` and lists any that fail.
- Exit codes: `journey.sh` 0 all passed, 1 a case failed, 3 nothing failed but a case was blocked; `bench.sh` 0 or 1.
- `up` refuses while the lane's bench is up (`down` first) and when 6 GB or less memory is available (the
  `available` column of `free -g`, not `free`). It needs Xvfb, openbox, AT-SPI (`at-spi2-core`), ffmpeg and `agent-desktop` (`~/.local/bin`), all installed on the Workbox.
- To look at the window yourself while it is up: `. "$DATA/bench/env"`, then `agent-desktop snapshot --app assembly-desktop -i`
  lists the controls by accessibility id (`scripts/drive/ax.ts` shows how journeys press and type).
- A journey is a short TypeScript file in `scripts/journeys/` built on `scripts/drive/journey.ts`; `chat-steer` is the only one so far.
