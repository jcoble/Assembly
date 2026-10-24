# Assembly — rules for agents

Assembly is a desktop app for running coding agents (Claude Code, Codex) on this Mac and on remote machines.
The UI is SvelteKit in `app/`; the backend is .NET in `dotnet/`.

## Code

- Organize code by feature: each feature has its own folder, in the backend and in the app.
- One class per file. Keep classes small, with one job each.
- Keep it simple: the smallest change that does the job. No abstraction, option or fallback the task didn't ask for.

## Backend conventions

- A feature's calls are `POST /<feature>/<method>` with the args as the JSON body. They answer `{result}`, or 400
  `{error}` for a call that fails, and 404 for an unknown method. `dotnet/Assembly.Mac/Page/PageRoutes.cs` is the model.
- Every route needs the bearer token (`TokenGuard`), and the API listens on 127.0.0.1 only.
- Each feature lives in `dotnet/Assembly.Engine/<Feature>/`: a routes class plus small classes, one class per file.
- Anything that streams or changes goes to the page over the one WebSocket, as a named topic. Never make the page
  poll. The socket carries only chat's rows today; the first feature that needs a topic adds topics to it.
- Chat's existing REST routes (`/threads`, `/runs`, `/outbox`, `/agents`, `/machines`) stay as they are.
- `docs/backend-commands.md` lists the old UI's commands per feature and the old Rust handler behind each.

## Porting a feature: spec first, then pinned tests

- Each feature lane first drafts a short spec of what the feature should do, in plain user-level terms. It works
  from the old UI and Jesse's recorded decisions (`docs/port-product-decisions.md`), posts the spec to Jesse, and
  Jesse corrects it.
- Tests pin that spec. The old code (mac-command-bar 2089290, listed in `docs/backend-commands.md`) is a reference
  for details such as formats and edge cases. It is never the source of truth.
- Pinned tests live in `dotnet/Assembly.Tests/Pinned/<Feature>/` and `app/test/pinned/<feature>/`. Commit them on
  their own, before the port.
- On a lane branch, pinned tests may fail. They land on main only together with the code that makes them pass:
  `scripts/run-pinned.sh` runs them on every push to main and fails if any fails, is skipped (other than a MacFact
  off macOS) or no longer runs.
- After that, never edit or delete a pinned test without Jesse's OK. If one looks wrong, stop and ask him.
  `scripts/check-pinned.sh` fails on any change to a pinned file he hasn't approved, locally and on every push to main.

## Commits

- Every commit message ends with `Committed-by: <who did the work>`, for example `Committed-by: Claude` or `Committed-by: Codex`.
- Never add a `Co-Authored-By` line or any other co-author trailer.
- One feature per commit.

## Checking a feature

- Check every feature with both Claude Code and Codex, on the Mac and on Workbox Test: `scripts/check4.sh`, with this
  commit's backend running locally and its host deployed (`dotnet/scripts/workbox.sh serve`). It writes
  `evidence/<commit>/four-way/`; commit that folder with the feature.
- A lane may not call a feature checked without a passing summary for its commit. CI fails a push to main whose
  feature commits have none (`scripts/check-evidence.sh`). Never write or edit evidence by hand.
- Window screenshots before and after go in `evidence/<commit>/four-way/window/`; `scripts/check4.sh window` lists
  them in the summary.
- Remote tests of the app run only on Workbox Test (SSH alias `workbox-test`).
- Real-window checks (the lag meter, memory) run on the Mac: pull the lane's branch into a Mac lane and run
  `shell/assembly.sh start --counters` there.

## Lanes

- Parallel work runs in lanes: `scripts/lane.sh new <name>` makes `~/dev/work/worktrees/Assembly/<name>` on branch
  `lane/<name>`, with its own ports, data folder, app id and Workbox Test host (`scripts/lane-env.sh`).
  `scripts/lane.sh remove <name>` tears it down; `scripts/lane.sh list` shows them. Main keeps the defaults.
- Lanes work only on agent-workbox (SSH alias `workbox`), in their own lane folders, and on Workbox Test
  (`workbox-test`), on their own host and checkout there. No other machine. The Mac runs the real-window checks, in a
  lane adopted there (`scripts/lane.sh adopt`).
- On agent-workbox, never touch the old Assembly host (`assembly-remote`), `tanstack-relay`, `tanstack-agent-server`,
  or Jesse's own sessions and data.
- Builds share the machine: run at most 2 or 3 .NET builds at once across lanes, each with `MSBUILDDISABLENODEREUSE=1`.

## Safety

- Never open the old Assembly data folders: `~/Library/Application Support/dev.blackcolours.MacCommandBarWebviewPreview`
  and `~/.local/share/assembly`.
- Stop processes only by pid, and only processes you started.
