# Assembly — rules for agents

Assembly is a desktop app for running coding agents (Claude Code, Codex) on this Mac and on remote machines.
The UI is SvelteKit in `app/`; the backend is .NET in `dotnet/`.




Assembly is a desktop workbench for coding agents: chat sessions beside an editor, terminal, Git and a browser, on the user's own provider subscriptions. The Tauri app (SvelteKit in tauri-svelte-preview/src, Rust in tauri-svelte-preview/src-tauri) runs Codex, Claude Code and Antigravity through ACP adapters it packages itself. The same Rust backend runs headless on remote machines over SSH, and the Flutter app in mobile/ drives sessions from an Android phone.

Jesse (the owner) wrote this file to set defaults; what he says in a task wins over it. When a rule here gets in the way of the task, stop and tell him rather than quietly working around it.

What we don't trade away
Fast and light. The app exists because the other tools are slow and heavy. Low CPU, RAM and GPU and a responsive UI come before any feature; a correct feature that makes the app slower is a defect. The regressions we keep finding: too much data per Tauri command or websocket message, animations that never stop, timers and listeners left running, objects not disposed, and processes that keep running after the user cancels. Filtering, counting and paging over SQLite happen in the SQL query, not in Rust or TypeScript after loading rows.
Animations end. Short animations tied to an interaction (hover fades, menu pops, 150-250ms transform or opacity slides) are welcome. Loops at rest (pulsing dots, shimmer, spinning gradients) repaint every frame forever: a loop may run only while real work is running.
Every timer and async call has an owner that stops it. async/await with an AbortSignal instead of .then chains; the owner clears its timers on destroy; real backend work can be cancelled. The pre-commit hook runs tauri-svelte-preview/scripts/scanSchedulers.ts, which also rejects CSS :has() and Tailwind has-*. Never add an allow-list entry without a stated reason.
Remote is not second class. Desktop to a remote backend over SSH, and phone to desktop or remote over Tailscale, are everyday use. A feature that only works locally is unfinished.
Open source. The code, the roadmap and the reasoning are public.
From Jesse: I want the simplest system that makes the right behaviour obvious. If code is more complicated than its problem, simplify it rather than build around it. Don't add structure to look thorough. Find the real constraint first, then change only what the task needs.

Words we use
You: the agent reading this. Jesse or the owner: who you are working for.
User: a person using Assembly. Provider: Codex, Claude Code or Antigravity.
Adapter: the provider's ACP process Assembly launches. It is disposable: once a turn and everything it started are idle, suspend_if_quiescent detaches it and stops the process; the next send relaunches it and resumes the same provider session. Never keep idle adapters alive for quicker switching, and never call a suspended session "live".
Session: two records. The Assembly session (ownedId) is the SQLite record the UI shows: journal, draft, attachments, workspace state. The provider session (nativeSessionId) owns the model's transcript and context; Assembly stores the ID and does not replay its journal as context.
Turn: one prompt and all the work it causes. Machine: where a backend runs, local or remote.
Things that do real damage
Killing by name. Stop only processes whose PID you saved when you started them. pkill -f, pgrep | kill and anything else that matches a name or path can hit your own agent (its command line holds the worktree path) or another lane's servers.
Touching the owner's data. The real app data folder (holding sessions.db; on the Mac /Users/blackcolours/Library/Application Support/dev.blackcolours.MacCommandBarWebviewPreview) and ~/.local/share/assembly on any machine are in use. You may read or copy from them. Never open them read-write, start a server against them, or clean them up. Copy test data in; never symlink. Test apps start from a copy of real data, not an empty database: an empty database is a bad test. Copy with sqlite3 -readonly <sessions.db> ".backup <dst>" (never a raw copy of a live database) into the test app's own data folder; fourScenarios.ts start --seed does this on the workbox. Data flows one way, into the copy. Before the first launch, delete every saved machine except Workbox Test and the cached remote chats (see docs/operations/four-scenario-check.md), because Settings → Connections signs in by SSH to every saved machine just by opening.
Connecting to the owner's machines. Remote tests use only the "Workbox Test" container (SSH alias workbox-test, see tools/workbox-test/README.md). Install and connect is fine from the test app, against Workbox Test only. Never connect a test app to "Agent Workbox", "SD" or any other saved machine: its sessions land in the owner's session list.
git reset --hard. It has destroyed uncommitted work. Use git stash push -m, git restore -- <path> after reading its diff, or git reset --soft HEAD~1. Before anything that can discard changes, run git status --short and say what would be lost.
Providers behave the same
Anything an ACP adapter or provider CLI supports must work the same for every provider that is installed and signed in. Don't ship a feature for one provider and leave the rest for later. When a provider really can't do something, decide that per provider and show it, rather than hiding it.
A provider-specific branch lives under tauri-svelte-preview/src-tauri/src/agent_conversation/providers/ and carries a comment citing the evidence that the provider differs: an adapter source line, a recorded response, or a test. The pre-commit check counts branches on provider names in every other non-test file and fails a file that goes above its baseline (zero for new files).
Defaults never name a provider. The Helper, a default model or agent, or anything that picks one chooses among the providers this user has set up, and says so when none are.
Provider quirks stay in providers/ and normalization/, so the session manager and the UI never ask which provider they are talking to. The UI shows state; it doesn't decide it.
Adapters are repackaged daily by .github/workflows/provider-updates.yml when upstream releases.
Where code lives
Rust (tauri-svelte-preview/src-tauri/src/): agent_conversation/ holds manager (ordering, commit and lifecycle), normalization/ (the only raw ACP parsing), history/ (the only transcript readers), providers/, remote/ (wire.rs is the only definition of the remote frames; server/ owns dispatch) and phone/ (phone routes, which write through the shared commands). Also git/, workflow/, files/, terminal/, browser/, helper/.

Frontend (tauri-svelte-preview/src/lib/shell/): one folder per feature, such as conversation/, messages/, git/, files/, terminal/, browser/, providers/, settings/, workflows/. Import a feature's public files only. The pre-commit check rejects imports of another feature's internals (timeline row components, terminal/ghosttyFactory, browser annotation and bounds files) and new files at paths the module refactor (TSK-1512) removed.

New frontend and tooling code, one-off scripts included, is TypeScript; no new .mjs files.

Check every place the change reaches
Changes here usually fail by being incomplete rather than wrong: the path you tried works and the one next to it doesn't. Before calling work done, name which of these you covered:

Every way in. The chat view, Settings, the command palette and the phone often reach the same feature. Cover them all, not only the one you found.
Every provider. See above. Chat changes are proved for Claude Code and Codex at minimum.
Local and remote. A remote session goes through remote/wire.rs to a backend that may be a different build (it is released on every merge to main). Change the frames on both sides at once, and a phone/ route together with the Flutter app.
Agents and workflows. If a user can trigger it, an agent or a workflow will want to. Put the behaviour in a service function, not in the UI handler.
The way back out. Anything you can open, start or hide needs its close, stop or show, and a way to see which state it is in.
Testing
Test first for any behaviour change: write the test, watch it fail, then make it pass.
The commit-msg hook rejects a commit whose subject starts with fix unless it stages a test file (*.test.ts, *_test.dart, a tests/ folder) or a Rust #[test], or the body has a line No-test: <reason>.
Run only what you touched: pnpm --dir tauri-svelte-preview test:<name> (or node --experimental-strip-types <file>.test.ts), cargo test --manifest-path tauri-svelte-preview/src-tauri/Cargo.toml <name>, check:svelte. Full suites belong to CI.
Test behaviour and logic. No tests that mirror the implementation or only check that a callback is wired. No tests on component structure, layout, panels, menus, styling, colour tokens or contrast: the UI changes daily and those tests only get rewritten to match it.
A test that needs a sleep or polling to pass is wrong; wait on the event that marks completion.
Every change gets the four-scenario check (docs/operations/four-scenario-check.md), with no exemption for changes that look small: Claude Code and Codex, each on a local session and on Workbox Test. Several changes may share one run if it exercises each of them. Report the four-row table with evidence; a scenario you can't run is reported blocked with the exact error. Each worktree has its own test-app run: fourScenarios.ts start --worktree <path> and stop --worktree <path> use ~/assembly-proof/current-<folder name>-<path hash> so equal folder names in different locations stay separate. The hash uses the canonical worktree path. Without --worktree, commands use the current directory's Git worktree. Different worktrees may run together when more than 6 GB of RAM is available; starting the same worktree twice is refused. Stopping one worktree leaves the others running.
The four scenarios only cover chat. A change the owner can see or use also gets a feature check: open that feature in the test app seeded with real data (long names, many projects and worktrees, large chats), use it as the task describes, screenshot it, look for anything cut off, repeated or empty, and add a "Feature check" line with the Drive link to the PR.
That check and any UI proof use the real native app with real test data, driven the way a person uses it. On Linux the app runs under Xvfb at 1710x990, driven with assembly-native-ui (screenshots, clicks, keys); Codex may use its computer-use tool. A browser preview is not proof: it has no app data. Use a headless browser at 1710x990 only for real web pages.
Save screenshots as ~/Workbox/screenshots/TSK-<id>-<what>-<before|after>.png and run evidence add <file> --task TSK-<id> --desc "<what it proves>"; evidence publish TSK-<id> gives the Drive links for the PR and the task. Close every browser and app you opened.
Review guidelines
For every reviewer, the review bot included: flag only problems that block. A bug, something the task asks for that is missing or broken, a security hole an agent could hit by accident, lost data, or a merge or release risk is a finding. Naming, wording, style, comments, test tidiness, unlikely edge cases, refactors and nicer ways to write the same thing are not; leave them out. On a later push, check the earlier findings and what their fixes changed; don't look for new small things.

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
  `{error}` for a call that fails, and 404 for an unknown method. `dotnet/Assembly.Engine/Features/FeatureCalls.cs` does it for every feature.
- Every route needs the bearer token (`TokenGuard`), and the API listens on 127.0.0.1 only.
- Each feature lives in `dotnet/Assembly.Engine/<Feature>/` when every machine serves it (listed in
  `Engine/Features/MachineFeatureList.cs`), or in `dotnet/Assembly.Mac/<Feature>/` when only the Mac's window needs it
  (listed in `Assembly.Mac/Features/MacFeatureList.cs`): a routes class plus small classes, one class per file.
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
  `dotnet/Assembly.Mac/Preferences/README.md` explains it step by step.
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

The workbox
Jesse works in Assembly's own Terminal tab on the workbox: the shell is started by the Assembly backend there and reached over the app's one SSH tunnel, and it ends whenever that backend restarts. How it works and what can go wrong: docs/remote-terminal.md.
Run free -g before a Rust build, a Tauri build or an app launch. If available is 6 GB or less, wait. One heavy job at a time per lane; prefer cargo test <name> to whole crates.
/tmp is a RAM disk: never put a cargo target folder or other big build output there.
Worktrees go in /home/blackcolours/dev/work/worktrees/mac-command-bar/<branch> (on the Mac, the same path under /Users/blackcolours). Remove yours with git worktree remove once it is merged or abandoned; report a dirty one instead of deleting it.
Commits and pull requests
Hooks live in .githooks (git config core.hooksPath .githooks once per clone). Pre-commit rejects a source file over 600 lines or over its entry in tauri-svelte-preview/scripts/commitChecks.baseline.json. Never raise a baseline; when a baselined file shrinks, lower its entry. New tests for a baselined file go in a small new file.
Branch names: tsk-<id>-<short-slug>, lowercase. On those branches the message needs Task: TSK-<id> https://app.notion.com/p/<page-id>.
Every message needs Architecture: unchanged, or a staged main-architecture-explained.html with a change-log row. The last line is Committed-by: <who did it>. No Co-Authored-By lines.
Subject in plain language, conventional style: fix(chat): reopened sessions keep their order. Body: what was wrong, then what changed. Read git diff --cached first; commit only your work.
One task, one branch, one PR: each step is a commit on the task's branch. Merging a tsk-<id> PR closes the task, so a PR opens when the task is complete or Jesse wants to test it.
Don't open a PR unless asked. Merge only with Jesse's permission, as ALLOW_MERGE=1 gh pr merge <n> --merge, once checks pass and the review bot has nothing open. If he says "I merge" for a PR, leave it to him.
A PR is one request. UI changes need before and after screenshots; motion needs a short video. Evidence goes to Drive and is linked from the PR and the task; never commit screenshots.
End the PR body with the model and harness that did the work (for example Model: Claude Opus · Harness: Claude Code). Code, comments and commit messages don't name AI tools.
Looking after a PR means handling what is new since your last push: confirm each reported problem in the code, fix it if real, otherwise reply with why not.
Scope: KISS and YAGNI
The smallest change that does the job wins. If two designs work, ship the one with fewer parts. If a design needs more than a few sentences to explain, simplify it first.
Build nothing for imagined futures: no new abstraction, config option, flag, fallback path or wrapper the task didn't ask for, and no drive-by refactors. One problem per change.
One way only. A change is done when the old mechanism is deleted. No compatibility bridges, legacy fallbacks, flags that keep both alive, or half-migrated stores. If the old way can't go yet, the change isn't finished. When you find an old/new split, removing one side is part of your task.
Reaching Jesse
A question for Jesse, or a blocker you can't get around, goes on the Build Board right away (board_update), with enough context to answer without reading the chat; the board notifies his desktop. Only the main orchestrator messages his phone (notify: true), for anything reasonable (a question, something ready to test, a blocker, a finished batch, an emergency), not for routine progress; every other session posts to the Build Board, which notifies his desktop. Until he answers a board question, his phone gets at most 3 of these in 30 minutes and 5 in an hour. A task blocked on him goes on hold; keep working on everything else.
Anything he needs to stay caught up (PR ready, check failed, blocker, change from plan, refused action) goes on the board in the same turn. Mark questions answered when they are. Helpers can't post; they tell their orchestrator.
Start agent lanes as Assembly helper sessions (start_session), never hidden codex exec. Send review-bot findings back to the lane that built the PR until the bot is clean.
The main orchestrator decides a paused workflow step itself (decide_step, with its why) when the findings and the spec give enough to decide. A gate, or a pause that needs Jesse's judgment (scope, a product choice, risk), goes on the Build Board with a phone notification instead.
Tasks, plans and docs
Tasks live in Notion. For a Notion-tracked task, follow .claude/skills/work-tasks/SKILL.md (also .codex/skills/work-tasks): the plan, dated updates, proof and review go on the task page, and the PR links the task and its proof.
A sub-agent sees none of the conversation, the owner's messages or pasted images. Its prompt must carry every requirement; a paraphrase from memory drops some.
main-architecture-explained.html is the one description of how the app works. Change it when a boundary, lifecycle, storage or wire decision changes, rewriting the affected text rather than adding a second account. Leave out what the code already says: field lists, control flow, file catalogues, PR summaries. A local reason belongs in a comment next to the code.
Explain anything long with a self-contained HTML page (diagram, mockup, before and after).
docs/operations/ holds maintainer procedures. Plans, recon notes and lane reports that are committed go in docs/plans/<YYYY-MM-DD>/, never the repo root. Scratch stays out of the worktree.
Plain English in everything a person reads. Spell terms out; no invented shorthand.
The build board
The owner reads the Board (top bar) to see what is running and what needs them. Write to it with the Assembly MCP tool board_update.
The session the owner gave a task to keeps one card for it; the main orchestrator may update any card. Helpers and workflow workers don't post. Key: the task key if there is one, else a name no other session would pick.
Post when you start, update when the state changes, and use needs_you only when the owner must act, with steps saying what to do. Link run_id and pr_url.
Record every question you ask the owner on the board before asking it, in the same turn (board_update with question, title, recommended), and start the chat question with the Q<n> it returns. When it is answered anywhere, mark it with answer; to ask it again, send a new question under the same key.

## Safety

- Never open the old Assembly data folders: `~/Library/Application Support/dev.blackcolours.MacCommandBarWebviewPreview`
  and `~/.local/share/assembly`.
- Stop processes only by pid, and only processes you started.
