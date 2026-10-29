# Assembly — rules for agents

Assembly is a desktop workbench for coding agents: chat sessions beside an editor, terminal, Git and a browser, on the
user's own provider subscriptions. It runs Claude Code and Codex (Antigravity is kept and comes later) on this computer and on remote machines. The UI is
SvelteKit in `app/`, inside a Tauri shell in `shell/`; the backend is .NET in `dotnet/`, and the same backend runs
headless on remote machines.

Jesse (the owner) wrote this file to set defaults; what he says in a task wins over it. When a rule here gets in the
way of the task, stop and tell him rather than quietly working around it.

## What we don't trade away

- Fast and light. The app exists because the other tools are slow and heavy. Low CPU, RAM and GPU and a responsive UI
  come before any feature; a correct feature that makes the app slower is a defect. The regressions we keep finding:
  too much data per socket frame, animations that never stop, timers and listeners left running, objects not
  disposed, and processes that keep running after the user cancels. Filtering, counting and paging over SQLite happen
  in the SQL query, not in C# or TypeScript after loading rows.
- Animations end. Short animations tied to an interaction (hover fades, menu pops, 150-250ms transform or opacity
  slides) are welcome. Loops at rest (pulsing dots, shimmer, spinning gradients) repaint every frame forever: a loop
  may run only while real work is running. In code that means a loop is written only under `[aria-busy="true"]` (or
  Tailwind's `aria-busy:` or `group-aria-busy:` variant), and the element sets `aria-busy` only while the work runs.
- Every async call has an owner that stops it. async/await with an AbortSignal instead of `.then` chains; real
  backend work can be cancelled. `app/scripts/guards.sh` runs before every commit and on every push to main.
- Remote is not second class. Desktop to a remote backend over SSH, and phone to desktop or remote over Tailscale
  (not ported yet), are everyday use. A feature that only works locally is unfinished.
- Open source. The code, the roadmap and the reasoning are public.
- From Jesse: I want the simplest system that makes the right behaviour obvious. If code is more complicated than its
  problem, simplify it rather than build around it. Don't add structure to look thorough. Find the real constraint
  first, then change only what the task needs.

## One way to do each thing

There is one path for each kind of work, so the right way is the easy way. There are no allowlists, exception lists
or carve-outs anywhere, and nobody adds one: a rule either holds for every file or it is changed, with Jesse's OK.

- No timers or ticks in `app/`, the old Svelte page, kept until the GPUI window (`desktop/`) replaces it. Nothing in
  `app/src` uses `setTimeout`, `setInterval`, `requestAnimationFrame`, `queueMicrotask`, Svelte's `tick()`,
  `AbortSignal.timeout`, or anything else that waits on time. What needs time comes from the backend, which pushes it
  (one shared backend clock), from CSS (an animation that ends), or from an event (`ResizeObserver`, a shell event). A
  timer in a row runs once per row, so on the session rail it fires thousands of times. If you think you need one,
  ask Jesse; there's no list to add yourself to.
- In `desktop/`, anything that ticks on a schedule (an elapsed time, a relative time) subscribes to the
  backend clock's topic: one shared clock, so no window thread is left running. The window's own timers are only for
  animations, frame waits and a debounce, and each is a Task held by its view, so it ends with the view.
  The one exception is the lag meter (`desktop/src/features/meter/`): its 100 ms stall tick and 5 s report timer run
  only while `ASSEMBLY_METER` is set.
- No CSS `:has()`, and no Tailwind variant that starts with `has-` or contains `-has-`, which compile to it. Use a class or data attribute on the element
  that owns the state, a custom property inherited down, or a pseudo-class scoped to the element that has it.
- One transport. The page talks to the backend over one WebSocket, through one file (`app/src/lib/socket.ts`, built
  in step B4 of `docs/plans/2026-10-26/implementation-plan.md`). Nothing else opens a socket or calls `fetch`. Until
  that lands, `app/src/lib/call.ts` is the one HTTP path and is being replaced; add no second way.
- Tauri only in `app/src/lib/host/`, and every window, document and media-query listener too, in any form
  (`addEventListener(`, `<svelte:window>`, `<svelte:document>`, `matchMedia(`). Features subscribe to what host
  exposes; events on an element use Svelte's `on…` attributes.
- async/await only: no `.then`, `.catch` or `.finally`.
- Every save is atomic. One save route makes one write, unless several writes run inside one transaction. A write
  commits before anything is told about it.
- The backend follows Clean Architecture, as in https://codewithmukesh.com/blog/clean-architecture-dotnet/: the
  core is in layers (Domain, Application, Infrastructure, pointing inward), and everything else is one folder per
  feature, `Assembly.Application/Features/<Feature>/`, which never uses another feature's folder. No repository:
  each feature writes its own plain SQL, reading through `IAppDatabase` and writing through `IAtomicWriter`. No
  folder holds more than 30 files. The layout is in section 4 of the plan.
- Use a shadcn-svelte or AI Elements component when one exists, added from the registry with
  `pnpm exec shadcn-svelte add <name>` from `app/` (the pinned CLI in the lockfile, never `pnpm dlx ...@latest`),
  never a hand-built look-alike; say which component in the PR. A component from the registry meets the same rules
  as any other code: if it fails the guard on add (a timer, a `.then`, a loop at rest, a `has-*` variant), edit it to
  meet the rules and say in the PR what you changed. The packages a component brings (`bits-ui` and the rest) are
  pinned to the exact version installed.
- Heights and spacing values are set once in the theme variables in `app/src/app.css`; components use those names,
  never a raw size.

## Words we use

- Session: one of Assembly's chats, the word ACP uses. Never name anything about them "thread": code, routes, files,
  tables or text.
- The Assembly session is the stored record the window shows (`session_id`); the provider session owns the model's
  transcript and context (`acp_session_id`). Store the provider ID; never replay the window's journal as model context.
- You: the agent reading this. Jesse or the owner: who you are working for.
- User: a person using Assembly. Provider: Claude Code and Codex today; Antigravity is kept and comes later.
- Adapter: the provider's ACP process Assembly launches. It is disposable: once a turn and everything it started are
  idle, it is stopped; the next send relaunches it and resumes the same provider session. Never keep idle adapters
  alive for quicker switching, and never call a stopped session "live".
- Turn: one prompt and all the work it causes. Machine: where a backend runs, local or remote.

## Things that do real damage

- Killing by name. Stop only processes whose PID you saved when you started them. `pkill -f`, `pgrep | kill` and
  anything else that matches a name or path can hit your own agent (its command line holds the worktree path) or
  another lane's servers.
- Touching the owner's data. Jesse's data folders are in use: this app's own, `~/assembly-dotnet` (main's default in
  `scripts/lane-env.sh`), and the old app's, `~/Library/Application Support/dev.blackcolours.MacCommandBarWebviewPreview`
  and `~/.local/share/assembly`. Never open them, start a server against them, or clean them up. Each lane has its
  own data folder (`scripts/lane-env.sh`). Test with real-looking seed data (long names, many projects, large chats),
  not an empty database: an empty database is a bad test. Copy approved test data in; never symlink it. Data flows
  one way into the test folder. Before launch, remove saved machines other than Workbox Test and cached remote
  sessions from the test data. There is no Assembly seed-and-sanitize command yet; see
  `docs/operations/four-scenario-check.md`. The prohibition on opening the owner's data still applies.
- Connecting to the owner's machines. Remote tests use only the "Workbox Test" container (SSH alias `workbox-test`).
  Never connect a test app to "Agent Workbox", "SD" or any other saved machine: its sessions land in the owner's
  session list.
- `git reset --hard`. It has destroyed uncommitted work. Use `git stash push -m`, `git restore -- <path>` after
  reading its diff, or `git reset --soft HEAD~1`. Before anything that can discard changes, run `git status --short`
  and say what would be lost.

## Providers behave the same

- Anything an ACP adapter or provider CLI supports must work the same for every provider that is installed and
  signed in. Don't ship a feature for one provider and leave the rest for later. When a provider really can't do
  something, decide that per provider and show it, rather than hiding it.
- A provider-specific branch lives where the adapters' updates are read (`dotnet/Assembly.Application/Common/Acp/V1/`)
  and carries a comment citing
  the evidence that the provider differs: an adapter source line, a recorded response, or a test.
  `scripts/lint.d/30-provider-branches.sh` rejects provider-name branches outside that folder; there is no baseline.
- Defaults never name a provider. A default model or agent, or anything that picks one, chooses among the providers
  this user has set up, and says so when none are.
- Provider quirks stay in the agent code, so the rest of the backend and the UI never ask which provider they are
  talking to. The UI shows state; it doesn't decide it.

## Check every place the change reaches

Changes here usually fail by being incomplete rather than wrong: the path you tried works and the one next to it
doesn't. Before calling work done, name which of these you covered:

- Every way in. The chat view, Settings, the command palette and the phone (not ported yet) often reach the same
  feature. Cover them all, not
  only the one you found.
- Every provider. See above. Chat changes are proved for Claude Code and Codex at minimum.
- Local and remote. A remote session goes to a backend that may be a different build. Change both sides of the wire
  at once.
- Agents and workflows. If a user can trigger it, an agent or a workflow will want to. Put the behaviour in the
  backend, not in the UI handler.
- The way back out. Anything you can open, start or hide needs its close, stop or show, and a way to see which state
  it is in.

## Code

- Organize code by feature: each feature has its own folder, in the backend and in the app.
- One class per file. Keep classes small, with one job each.
- The GPUI window lives in `desktop/src/core/` and `desktop/src/features/<feature>/` (see `desktop/README.md`).
  Import a feature's public files only, never another feature's internals.
- `scripts/lint.d/20-file-size.sh` rejects source files over 600 lines; Assembly has no file-size baseline to raise.
  Split an oversized file. Put new tests in small files.
- TypeScript only. No .js, .mjs or .cjs files anywhere in the repo; a script that needs node is a .ts file run by node
  directly (type stripping). If a tool truly needs a JS config file, ask Jesse. `scripts/lint.d/00-typescript-only.sh`
  enforces it.

## Timers, ticks and :has() in app/

- These rules cover `app/`, the old Svelte page. `desktop/` follows its own bullet in "One way to do each thing".
- `app/scripts/guards.sh` enforces "One way to do each thing" on the page's source, before every commit and on every
  push to main. It is locked like the pinned tests (see "Who may OK" below).
- The guard still reads `app/scripts/guards-allowed.txt` until step B10 of the plan deletes it. Nothing is ever added
  to it.
- Per-row components are everything under `app/src/lib/chat/` and any file named `<Name>Row.svelte`. Name a component
  drawn once per row that way, wherever it lives; the script checks it for tick and frame waits.

## Backend conventions

- One helper on each side does the repeated plumbing, so it can't be forgotten: the page's `app/src/lib/call.ts`
  (`call()` and `save()`) and the backend's `Assembly.Infrastructure/Http/FeatureCalls.cs` (`Map(method, handler)`).
  Features never call `fetch` or map a route themselves (`app/scripts/guards.sh`, `scripts/lint.d/40-routes.sh`).
  Both are replaced by the socket and its dispatcher (plan steps B4 to B7); a feature still uses them until then.
- Every route needs the bearer token (`TokenGuard`), and the API listens on 127.0.0.1 only.
- Each feature lives in `dotnet/Assembly.Application/Features/<Feature>/`, and its outside systems (files, processes,
  native calls) in `dotnet/Assembly.Infrastructure/<Feature>/`: small classes, one class per file. Until step B7, its
  routes class is in `Assembly.Infrastructure/Http/`, listed in `MachineFeatureList.cs` when every machine serves it or
  `DesktopFeatureList.cs` when only the desktop's window needs it. `ArchitectureTests` checks the layers.
- Anything that streams or changes goes to the page over the socket, as a named topic. Never make the page poll.
- Work nobody wants any more stops, in the page and here. `call()` makes each request's abort signal, and a newer
  call with the same key aborts the older one. A handler passes its `cancellationToken` to everything it awaits: the
  database, files, and child processes such as git, which are killed when it fires (CA2016 is a build error). A call
  forwarded to another machine carries the cancel there too. Saves are the exception: once sent they finish.
  `dotnet/Assembly.Application/Features/Preferences/README.md` explains it step by step.
- `docs/backend-commands.md` lists the old UI's commands per feature and the old Rust handler behind each.

## Testing

- Test first for any behaviour change: write the test, watch it fail, then make it pass.
- The commit-msg hook rejects a subject starting with `fix` unless the staged files include a test under
  `dotnet/Assembly.Tests/`, `app/test/`, `desktop/tests/`, a `scripts/*.test.sh`, or a `tests.rs` or `*_tests.rs`
  under `desktop/src/`; otherwise the body must include `No-test: <reason>` (`scripts/check-commit-msg.sh`).
- Run only what you touched: `pnpm --dir app test` with a file filter, `dotnet test --filter <name>` from `dotnet/`,
  or `cargo test --locked <name>` from `desktop/`. Full suites belong to CI.
- Test behaviour and logic. No tests that mirror the implementation or only check that a callback is wired. No tests
  on component structure, layout, panels, menus, styling, colour tokens or contrast: the UI changes daily and those
  tests only get rewritten to match it.
- A test that needs a sleep or polling to pass is wrong; wait on the event that marks completion.
- Every change gets the check its level calls for (see "Checking a feature": none, one tester, or the four-way).
  `docs/operations/four-scenario-check.md` explains setup, the four-row report and blocked results. Several changes
  may share a run only if it exercises each change and meets the commit-evidence rule below.

## Review guidelines

For every reviewer, the review bot included: flag only problems that block. A bug, something the task asks for that is
missing or broken, a security hole an agent could hit by accident, lost data, or a merge or release risk is a finding.
Naming, wording, style, comments, test tidiness, unlikely edge cases, refactors and nicer ways to write the same thing
are not; leave them out. On a later push, check the earlier findings and what their fixes changed; don't look for new
small things.

Every reviewer, the review bot included, is also the final gate on the four-way check ("Checking a feature"). Read
every run's report and open its evidence. Block the PR when the runs don't cover what the PR changes end to end, skip
the off-happy-path cases that matter for this change, are missing a run the change's level calls for or ran on a commit older than
the head, when the screenshots don't show what the report claims, or when the look and theme don't match the rest of
the app. Thin testing is a blocking finding, like a bug.

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
- Files first, then code. From B7 on, every feature port is two steps, each reviewed on its own.
  Step 1 creates every file the feature has in the reference layout (plan section 4): each type with its name,
  signatures and comment, and no logic, so the review looks at the layout alone. Step 2 brings the old code over,
  piece by piece, into those files.

## Reviews for every implementation step

- Before writing anything, a Claude Code subagent reads the step, what's on main, the plan and this file, and reports
  gaps or anything that needs changing.
- After the step, two code and spec reviews of its diff: one by a Claude Code subagent, and one by Sol 6.1
  (`codex exec -s read-only -m gpt-6.1-sol`).
- Both reviews ask for the smallest fix and flag over-engineering ("Scope: KISS and YAGNI"; what counts as a finding
  is in "Review guidelines"). Report each finding as fixed or dropped, with the reason.

## Checking a feature

Why this matters more than anything else here: tests, scenario scripts and reviews only show that the parts you
thought of do what you expected. A feature is the whole app coming together: the backend, the socket, the window,
layout, theme, timing, and real data from many sessions and machines. Everyone is confident in their own code, and
things still go wrong in ways nobody expected. The only way to find them is to run the real app and use the feature
the way a person would, thoroughly, every time. So the four-way UI check below is the proof a feature works.
Nothing else stands in for it, however sure you are of the code.

- **Which check.** The controller picks the level in each lane's spec, by what the change touches, and says why. A
  test alone never proves behavior: every level except the first runs the real app.

  | Change touches | Check | Proof |
  |---|---|---|
  | Scripts, build or CI config, docs, tests only | None: run the changed thing once | Its output in the PR |
  | Backend behavior: delivery, send rules, data, remote | One tester drives the app to where the result shows; local and Workbox Test only if it touches remote | 2 to 4 screenshots of where it lands: the message in the chat, its log line, its audit entry, the row's state |
  | Anything visible, or the chat or session flow | The four-way check below | The report table and screenshots |

  Backend behavior almost always shows somewhere in the app. If it shows nowhere, the tester says so: the change
  likely needs a log line or audit entry anyway. A fix after a failed check is rechecked by one tester on just what
  failed. One run may cover several PRs of a feature when it exercises each of them.

- **The four-way check.** Claude Code and Codex each test the feature in the real native app on the workbox: once
  with the app's own backend there (local), and once on sessions on Workbox Test, connected from Settings >
  Connections (remote). Four runs: Claude Code local, Claude Code remote, Codex local, Codex remote.
- **Use it like a person.** Mouse and keyboard through the UI, nothing else. The native Linux build runs under Xvfb
  at 1710x990 (`scripts/ui-check.sh start`, `shot <file>`, `stop`). Claude Code drives it with `assembly-native-ui`
  (shot, click, key) and `xdotool` for typing and scrolling; Codex may use its computer-use tool. Open every
  screenshot after each action; never assume a click worked. A browser preview, a socket script or a test is not
  this check.
- **What to look for.**
  - Does it look right: proportional, in the app's theme and style, nothing cut off, overlapping, repeated or
    empty. Use real-looking data: long names, many projects, worktrees and sessions, large chats.
  - Does it work end to end: every part of the feature, not just the easiest path.
  - Then leave the happy path: wrong or empty input, empty and very long lists, a slow or offline machine,
    reconnecting, closing and reopening, restarting, doing things twice or out of order.
  - Local and remote behave the same, and so do Claude Code and Codex.
- **Report.** Each run writes a short report of exactly what it did, as numbered steps: what it clicked or typed, what
  it expected, what it saw, and pass, FAIL or blocked. It lists the off-happy-path things it tried, and anything it
  couldn't check and why. A run that can't be done is blocked, with the exact error and a screenshot; never
  substitute a result.
- **Evidence.** Per PR, at most 4 screenshots (before and after where it matters) and one video of 2 minutes or less.
  They go to Google Drive with the `evidence` tool, never into this repo (docs/evidence.md): save them as
  `~/Workbox/screenshots/TSK-<id>-<what>.png` (or `.mp4`), then on agent-workbox run
  `evidence add <file> --task TSK-<id> --desc "<what it proves>"` and `evidence publish TSK-<id>`.
  `scripts/check4.sh window <commit> <file>...` records their names in the summary.
- **The PR table.** The PR body has one table with a row per run: tester, local or remote, commit, pass, FAIL or
  blocked, and links to its screenshots and video. Each run's report goes under the table, so the reviewer reads it in
  the PR. A blocked row shows the exact error. Fixes go back to the lane, and the fixed commit is rechecked by one tester on what failed. A lane may not call a feature done
  without its level's check passing on its final commit.
- **The regression check is not the four-way check.** `scripts/check4.sh` sends one turn per provider and machine and
  runs every landed feature's short socket scenario (`scripts/features/<feature>.sh`, see the README there), so a
  change that breaks something already landed is caught cheaply. Keep scenarios short (seconds). It writes
  `evidence/<commit>/four-way/` (the folder keeps its old name); commit that folder with the feature, since CI fails a
  push to main whose code commits have none (`scripts/check-evidence.sh`). Never write or edit evidence by hand. A
  passing summary only says the pieces still connect; it says nothing about whether the feature works for a person.
- Close every browser and app you opened, and verify their processes exited. Stop only your own recorded PIDs.
- Remote tests of the app run only on Workbox Test (SSH alias `workbox-test`).
- Real-window checks run in the native app on agent-workbox with `scripts/ui-check.sh` (see "Checking a feature").
Test first for any behaviour change: write the test, watch it fail, then make it pass.
The commit-msg hook rejects a commit whose subject starts with fix unless it stages a test file (*.test.ts, *_test.dart, a tests/ folder) or a Rust #[test], or the body has a line No-test: <reason>.
Run only what you touched: pnpm --dir tauri-svelte-preview test:<name> (or node --experimental-strip-types <file>.test.ts), cargo test --manifest-path tauri-svelte-preview/src-tauri/Cargo.toml <name>, check:svelte. Full suites belong to CI.
Test behaviour and logic. No tests that mirror the implementation or only check that a callback is wired. No tests on component structure, layout, panels, menus, styling, colour tokens or contrast: the UI changes daily and those tests only get rewritten to match it.
A test that needs a sleep or polling to pass is wrong; wait on the event that marks completion.
Every change gets the four-scenario check (docs/operations/four-scenario-check.md), with no exemption for changes that look small: Claude Code and Codex, each on a local session and on Workbox Test. Several changes may share one run if it exercises each of them. Report the four-row table with evidence; a scenario you can't run is reported blocked with the exact error. Each worktree has its own test-app run: fourScenarios.ts start --worktree <path> and stop --worktree <path> use ~/assembly-proof/current-<folder name>-<path hash> so equal folder names in different locations stay separate. The hash uses the canonical worktree path. Without --worktree, commands use the current directory's Git worktree. Different worktrees may run together when more than 6 GB of RAM is available; starting the same worktree twice is refused. Stopping one worktree leaves the others running.
The four scenarios only cover chat. A change the owner can see or use also gets a feature check: open that feature in the test app seeded with real data (long names, many projects and worktrees, large chats), use it as the task describes, screenshot it, look for anything cut off, repeated or empty, and add a "Feature check" line with the Drive link to the PR.
That check and any UI proof use the real native app with real test data, driven the way a person uses it. On Linux the app runs under Xvfb at 1710x990, driven with assembly-native-ui (screenshots, clicks, keys); Codex may use its computer-use tool. A browser preview is not proof: it has no app data. Use a headless browser at 1710x990 only for real web pages.
Save screenshots as ~/Workbox/screenshots/TSK-<id>-<what>-<before|after>.png and run evidence add <file> --task TSK-<id> --desc "<what it proves>"; evidence publish TSK-<id> gives the Drive links for the PR and the task. Close every browser and app you opened.


## Lanes

- Parallel work runs in lanes: `scripts/lane.sh new <name>` makes `~/dev/work/worktrees/Assembly/<name>` on branch
  `lane/<name>`, with its own ports, data folder, app id and Workbox Test host (`scripts/lane-env.sh`).
  `scripts/lane.sh remove <name>` tears it down; `scripts/lane.sh list` shows them. Main keeps the defaults.
  Each worktree has its own test-app run. Never start the same lane twice or stop another lane's run. Assembly uses
  `.lane` and `scripts/lane-env.sh`, not the old `fourScenarios.ts --worktree` path-hash scheme.
- Remove your lane once merged or abandoned, using `scripts/lane.sh remove <name>`, then verify with
  `git worktree list`. Report a dirty worktree and who owns the next action; never delete it silently.
- Lanes work only on agent-workbox (SSH alias `workbox`), in their own lane folders, and on Workbox Test
  (`workbox-test`), on their own host and checkout there. No other machine. The Mac runs the real-window checks, in a
  lane adopted there (`scripts/lane.sh adopt`).
- On agent-workbox, never touch the old Assembly host (`assembly-remote`), `tanstack-relay`, `tanstack-agent-server`,
  or Jesse's own sessions and data.
- Builds share the machine: run at most 2 or 3 .NET builds at once across lanes, each with `MSBUILDDISABLENODEREUSE=1`.
  Run `free -g` before a build or an app launch; if available is 6 GB or less, wait.
- One heavy job at a time per lane, including Rust builds and native app launches; prefer a filtered test to a
  whole crate. The shared memory check above applies to Rust builds as well as .NET.
- `/tmp` on agent-workbox is a RAM disk: never put big build output there, including Cargo target folders.
- Workbox Test setup and the files still needing a port are in `docs/operations/workbox-test.md`. The old app's
  remote Terminal implementation is not ported; do not assume this backend owns that shell or that restarting it
  is safe for the owner's Terminal. Terminals are planned in W4 of the implementation plan.

## Commits and pull requests

- Hooks live in `.githooks` (`git config core.hooksPath .githooks` once per clone; `scripts/lane.sh` sets it).
- On a `tsk-<id>-<short-slug>` task branch, the message needs
  `Task: TSK-<id> https://app.notion.com/p/<page-id>`. Assembly's lane branches remain `lane/<name>` as above.
- Every message needs `Architecture: unchanged`, or a staged change to `docs/architecture.md` describing the
  changed boundary, lifecycle, storage or wire decision (`scripts/check-commit-msg.sh`).
- Keep one task on one branch and in one PR, with each step as a commit. A task PR opens when the task is complete
  or Jesse asks to test it; opening one still requires his request.
- Every commit message ends with `Committed-by: <who did the work>`, for example `Committed-by: Claude` or
  `Committed-by: Codex`. Never add a `Co-Authored-By` line or any other co-author trailer.
- Subject in plain language, conventional style: `fix(chat): reopened sessions keep their order`. Body: what was
  wrong, then what changed. Read `git diff --cached` first; commit only your work. One feature per commit.
- Don't open a PR unless asked. Merge only with Jesse's permission. If he says "I merge" for a PR, leave it to him.
- An explicitly authorized merge uses `ALLOW_MERGE=1 gh pr merge <n> --merge`, after checks pass and the review
  bot has nothing open. Send review-bot findings back to the lane that built the PR until they are resolved.
- End the PR body with the model and harness that did the work.
- A PR is one request. UI changes need before and after screenshots; motion needs a short video. Never commit
  screenshots.
- Code, comments and commit messages don't name AI tools.
- Looking after a PR means handling what is new since your last push: confirm each reported problem in the code, fix
  it if real, otherwise reply with why not.

## Scope: KISS and YAGNI

- The smallest change that does the job wins. If two designs work, ship the one with fewer parts. If a design needs
  more than a few sentences to explain, simplify it first.
- Build nothing for imagined futures: no new abstraction, config option, flag, fallback path or wrapper the task
  didn't ask for, and no drive-by refactors. One problem per change.
- One way only. A change is done when the old mechanism is deleted. No compatibility bridges, legacy fallbacks, flags
  that keep both alive, or half-migrated stores. If the old way can't go yet, the change isn't finished. When you find
  an old/new split, removing one side is part of your task.

## Reaching Jesse

- A question for Jesse, or a blocker you can't get around, goes to the project thread right away, with enough context
  to answer without reading the chat. That task waits; keep working on everything else.
- The Build Board and `board_update` don't exist here yet; the Build Board is rebuilt in plan phase P3.

## Plans and docs

- Read the Notion page "Assembly plan and decisions"
  (https://app.notion.com/p/408394b0689d81b599fbe74c704a5986) before starting work: the order of work, Jesse's
  decisions in his own words, and the open questions. Never remove or reword his decisions there.
- Tasks live in Notion. For Notion-tracked work, follow `.claude/skills/work-tasks/SKILL.md` (also
  `.codex/skills/work-tasks`): the plan, dated updates, proof and review go on the task page; the PR links the task
  and its proof.
- `docs/architecture.md` is the maintained description of how Assembly works. When a boundary, lifecycle, storage
  or wire decision changes, rewrite the affected text rather than add a second account. Leave out field lists,
  control flow, file catalogues and PR summaries; put a local reason in a comment beside the code.
- `docs/operations/` holds maintainer procedures.
- A sub-agent sees none of the conversation, the owner's messages or pasted images. Its prompt must carry every
  requirement; a paraphrase from memory drops some.
- Plans, recon notes and lane reports that are committed go in `docs/plans/<YYYY-MM-DD>/`, never the repo root.
  Scratch stays out of the worktree.
- Explain anything long with a self-contained HTML page (diagram, mockup, before and after).
- Plain English in everything a person reads. Spell terms out; no invented shorthand.
