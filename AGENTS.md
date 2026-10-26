# Assembly — rules for agents

Assembly is a desktop workbench for coding agents: chat sessions beside an editor, terminal, Git and a browser, on the
user's own provider subscriptions. It runs Claude Code and Codex (Antigravity is kept and comes later) on this Mac and on remote machines. The UI is
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

- No timers or ticks in the app. Nothing in `app/src` uses `setTimeout`, `setInterval`, `requestAnimationFrame`,
  `queueMicrotask`, Svelte's `tick()`, `AbortSignal.timeout`, or anything else that waits on time. What needs time
  comes from the backend, which pushes it (one shared backend clock), from CSS (an animation that ends), or from an
  event (`ResizeObserver`, a shell event). A timer in a row runs once per row, so on the session rail it fires
  thousands of times. If you think you need one, ask Jesse; there's no list to add yourself to.
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
- The backend follows Clean Architecture, as in https://codewithmukesh.com/blog/clean-architecture-dotnet/: layers
  point inward, and features register their calls in one place. The layout is in section 4 of the plan.
- Use a shadcn-svelte or AI Elements component when one exists, added from the registry with
  `pnpm exec shadcn-svelte add <name>` from `app/` (the pinned CLI in the lockfile, never `pnpm dlx ...@latest`),
  never a hand-built look-alike; say which component in the PR. A component from the registry meets the same rules
  as any other code: if it fails the guard on add (a timer, a `.then`, a loop at rest, a `has-*` variant), edit it to
  meet the rules and say in the PR what you changed. The packages a component brings (`bits-ui` and the rest) are
  pinned to the exact version installed.
- Heights and spacing are set once in the theme variables in `app/src/app.css`, never per component.

## Words we use

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
  not an empty database: an empty database is a bad test.
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
- A provider-specific branch lives with the agent code (`dotnet/Assembly.Engine/Agents/`) and carries a comment citing
  the evidence that the provider differs: an adapter source line, a recorded response, or a test.
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
- TypeScript only. No .js, .mjs or .cjs files anywhere in the repo; a script that needs node is a .ts file run by node
  directly (type stripping). If a tool truly needs a JS config file, ask Jesse. `scripts/lint.d/00-typescript-only.sh`
  enforces it.

## Timers, ticks and :has() in the app

- `app/scripts/guards.sh` enforces "One way to do each thing" on the page's source, before every commit and on every
  push to main. It is locked like the pinned tests (see "Who may OK" below).
- The guard still reads `app/scripts/guards-allowed.txt` until step B10 of the plan deletes it. Nothing is ever added
  to it.
- Per-row components are everything under `app/src/lib/chat/` and any file named `<Name>Row.svelte`. Name a component
  drawn once per row that way, wherever it lives; the script checks it for tick and frame waits.

## Backend conventions

- One helper on each side does the repeated plumbing, so it can't be forgotten: the page's `app/src/lib/call.ts`
  (`call()` and `save()`) and the backend's `Assembly.Engine/Features/FeatureCalls.cs` (`Map(method, handler)`).
  Features never call `fetch` or map a route themselves (`app/scripts/guards.sh`, `scripts/lint.d/40-routes.sh`).
  Both are replaced by the socket and its dispatcher (plan steps B4 to B7); a feature still uses them until then.
- Every route needs the bearer token (`TokenGuard`), and the API listens on 127.0.0.1 only.
- Each feature lives in `dotnet/Assembly.Engine/<Feature>/` when every machine serves it (listed in
  `Engine/Features/MachineFeatureList.cs`), or in `dotnet/Assembly.Mac/<Feature>/` when only the Mac's window needs it
  (listed in `Assembly.Mac/Features/MacFeatureList.cs`): a routes class plus small classes, one class per file.
- Anything that streams or changes goes to the page over the socket, as a named topic. Never make the page poll.
- Work nobody wants any more stops, in the page and here. `call()` makes each request's abort signal, and a newer
  call with the same key aborts the older one. A handler passes its `cancellationToken` to everything it awaits: the
  database, files, and child processes such as git, which are killed when it fires (CA2016 is a build error). A call
  forwarded to another machine carries the cancel there too. Saves are the exception: once sent they finish.
  `dotnet/Assembly.Mac/Preferences/README.md` explains it step by step.
- `docs/backend-commands.md` lists the old UI's commands per feature and the old Rust handler behind each.

## Testing

- Test first for any behaviour change: write the test, watch it fail, then make it pass.
- Run only what you touched: `pnpm --dir app test` with a file filter, or `dotnet test --filter <name>`. Full suites
  belong to CI.
- Test behaviour and logic. No tests that mirror the implementation or only check that a callback is wired. No tests
  on component structure, layout, panels, menus, styling, colour tokens or contrast: the UI changes daily and those
  tests only get rewritten to match it.
- A test that needs a sleep or polling to pass is wrong; wait on the event that marks completion.
- Every change gets the four-scenario check (see "Checking a feature"), with no exemption for changes that look
  small: Claude Code and Codex, each on a local session and on Workbox Test.

## Review guidelines

For every reviewer, the review bot included: flag only problems that block. A bug, something the task asks for that is
missing or broken, a security hole an agent could hit by accident, lost data, or a merge or release risk is a finding.
Naming, wording, style, comments, test tidiness, unlikely edge cases, refactors and nicer ways to write the same thing
are not; leave them out. On a later push, check the earlier findings and what their fixes changed; don't look for new
small things.

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
- A change the owner can see or use also gets a feature check: open that feature in the app with real-looking data
  (long names, many projects, large chats), use it as the task describes, and look for anything cut off, repeated or
  empty.
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
  Run `free -g` before a build or an app launch; if available is 6 GB or less, wait.
- `/tmp` on agent-workbox is a RAM disk: never put big build output there.

## Commits and pull requests

- Hooks live in `.githooks` (`git config core.hooksPath .githooks` once per clone; `scripts/lane.sh` sets it).
- Every commit message ends with `Committed-by: <who did the work>`, for example `Committed-by: Claude` or
  `Committed-by: Codex`. Never add a `Co-Authored-By` line or any other co-author trailer.
- Subject in plain language, conventional style: `fix(chat): reopened sessions keep their order`. Body: what was
  wrong, then what changed. Read `git diff --cached` first; commit only your work. One feature per commit.
- Don't open a PR unless asked. Merge only with Jesse's permission. If he says "I merge" for a PR, leave it to him.
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

## Plans and docs

- A sub-agent sees none of the conversation, the owner's messages or pasted images. Its prompt must carry every
  requirement; a paraphrase from memory drops some.
- Plans, recon notes and lane reports that are committed go in `docs/plans/<YYYY-MM-DD>/`, never the repo root.
  Scratch stays out of the worktree.
- Explain anything long with a self-contained HTML page (diagram, mockup, before and after).
- Plain English in everything a person reads. Spell terms out; no invented shorthand.
