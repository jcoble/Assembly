# Assembly — rules for agents

Assembly is a desktop app for running coding agents (Claude Code, Codex) on this Mac and on remote machines.
The UI is SvelteKit in `app/`; the backend is .NET in `dotnet/`.

## Code

- Organize code by feature: each feature has its own folder, in the backend and in the app.
- One class per file. Keep classes small, with one job each.
- Keep it simple: the smallest change that does the job. No abstraction, option or fallback the task didn't ask for.
- TypeScript only. No .js, .mjs or .cjs files anywhere in the repo; a script that needs node is a .ts file run by node
  directly (type stripping). If a tool truly needs a JS config file, ask Jesse. `scripts/lint.d/00-typescript-only.sh`
  enforces it.

## Timers, ticks and :has() in the app

- Plan rule 11: one shared page clock, no polling, no tick or frame waits in per-row components, and no CSS `:has()`.
  A timer in a row runs once per row, so on the session rail it fires thousands of times.
- `app/scripts/guards.sh` enforces this on every push to main, and its allowlist is `app/scripts/guards-allowed.txt`.
  Both are locked like the pinned tests (see "Who may OK" below).
- The allowlist names files only, never lines or line numbers, and only files that still need it: the script fails
  on a listed file that no longer does anything the list allows.
- Per-row components are everything under `app/src/lib/chat/` and any file named `<Name>Row.svelte`. Name a component
  drawn once per row that way, wherever it lives; the script checks it for tick and frame waits.
- If you think you need a new timer or tick, ask the move thread or Jesse. Never add yourself to the allowlist.

## Backend conventions

- A feature's calls are `POST /<feature>/<method>` with the args as the JSON body. They answer `{result}`, or 400
  `{error}` for a call that fails, and 404 for an unknown method. `dotnet/Assembly.Mac/Page/PageRoutes.cs` is the model.
- Every route needs the bearer token (`TokenGuard`), and the API listens on 127.0.0.1 only.
- Each feature lives in `dotnet/Assembly.Engine/<Feature>/`: a routes class plus small classes, one class per file.
- Anything that streams or changes goes to the page over the one WebSocket, as a named topic. Never make the page
  poll. The socket carries only chat's rows today; the first feature that needs a topic adds topics to it.
- One helper on each side does the repeated plumbing, so it can't be forgotten: the page's `app/src/lib/call.ts`
  (`call()` and `save()`) and the backend's `Assembly.Engine/Features/FeatureCalls.cs` (`Map(method, handler)`).
  Features never call `fetch` or map a route themselves (`app/scripts/guards.sh`, `scripts/lint.d/40-routes.sh`).
  Calls go over HTTP; pushes (`/events`, `/live`, terminals) over WebSockets. Only those two files know it.
- Work nobody wants any more stops, in the page and here. `call()` makes each request's abort signal, and a newer
  call with the same key aborts the older one. A handler passes its `cancellationToken` (`HttpContext.RequestAborted`)
  to everything it awaits: the database, files, and child processes such as git, which are killed when it fires
  (CA2016 is a build error). A call forwarded to another machine carries the cancel there too. Saves are the
  exception: debounced, the newest replaces the rest, and once sent they finish.
  `dotnet/Assembly.Engine/Preferences/README.md` explains it step by step.
- Chat's existing REST routes (`/threads`, `/runs`, `/outbox`, `/agents`, `/machines`) stay as they are.
- `docs/backend-commands.md` lists the old UI's commands per feature and the old Rust handler behind each.

## Porting a feature: behavior list first, then pinned tests

- Each feature lane first drafts the feature's behavior list, `docs/behavior/<lane>.md`: what the feature should do,
  in plain user-level terms, one line each with an id (`base#1`, `base#2`, ...). It works from the old UI and
  Jesse's recorded decisions (`docs/port-product-decisions.md`), posts the list to Jesse, and Jesse corrects it.
- The top of the list says either "Draft by Claude, not corrected by Jesse" or "Corrected by Jesse on <date>",
  quoting or linking his correction. Only Jesse's correction changes it from a draft.
- Tests pin that list. Each pinned test names the line it pins in a comment, for example `// Pins: base#2`. The old code (mac-command-bar 2089290, listed in `docs/backend-commands.md`) is a reference
  for details such as formats and edge cases. It is never the source of truth.
- While the list is a draft, the lane writes its tests in `dotnet/Assembly.Tests/Draft/<Feature>/` (namespace
  `Assembly.Tests.Draft.<Feature>`) and `app/test/draft/<feature>/`. They aren't locked and follow the normal test
  rules on main. Once Jesse has corrected the list, make any changes his correction needs, then run
  `scripts/pin.sh <feature>`. It moves the tests into the pinned folders and refuses while the list is a draft.
- Pinned tests live in `dotnet/Assembly.Tests/Pinned/<Feature>/` and `app/test/pinned/<feature>/`. Commit them on
  their own, before the port.
- On a lane branch, pinned tests may fail. They land on main only together with the code that makes them pass:
  `scripts/run-pinned.sh` runs them on every push to main and fails if any fails, is skipped (other than a MacFact
  off macOS) or no longer runs.
- After that, a pinned test is changed or deleted only with an OK in `.pinned-approvals`. If one looks wrong, stop and
  ask. `scripts/check-pinned.sh` fails on any change to a pinned file without an OK, locally and on every push to main.
- Who may OK a change to a pinned test:
  - Jesse may OK any change.
  - The move thread (the Claude thread in Jesse's project that reviews the port) may OK only a change that doesn't
    alter what a test checks (a rename, a moved file, a fixed setup or helper, never a loosened assertion), or one
    that makes a test match the behavior list Jesse corrected, naming the list line.
  - The lane that wants a change can never OK it.
  - The move thread posts every OK it gives to Jesse, with the test before and after.
  - A change to what a feature should do waits for Jesse.

## Commits

- Every commit message ends with `Committed-by: <who did the work>`, for example `Committed-by: Claude` or `Committed-by: Codex`.
- Never add a `Co-Authored-By` line or any other co-author trailer.
- One feature per commit.

## Checking a feature

- Check every feature with both Claude Code and Codex, on the Mac and on Workbox Test: `scripts/check4.sh`, with this
  commit's backend running locally and its host deployed (`dotnet/scripts/workbox.sh serve`). It writes
  `evidence/<commit>/four-way/`; commit that folder with the feature.
- The plain check proves turns run, not that the feature works. A feature's lane writes its scenario,
  `scripts/features/<feature>.sh` (see the README there), from the corrected spec. `scripts/check4.sh` runs every
  scenario in every case, so a change that breaks a feature already landed fails its check. Keep a scenario short
  (seconds, not minutes) so a full run stays cheap. `--feature <name>` runs one scenario for quick runs while
  working; that run isn't evidence.
- A lane may not call a feature checked without a passing summary for its commit. CI fails a push to main whose
  code commits have none (`scripts/check-evidence.sh`). Never write or edit evidence by hand.
- Window screenshots before and after go in the project's private files, not this repo (they show machine names
  and paths). `scripts/check4.sh window <commit> <file>...` records their names in the summary.
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
