# Assembly — rules for agents

Assembly is a desktop app for running coding agents (Claude Code, Codex) on this Mac and on remote machines.
The UI is SvelteKit in `app/`; the backend is .NET in `dotnet/`.

## Code

- Organize code by feature: each feature has its own folder, in the backend and in the app.
- One class per file. Keep classes small, with one job each.
- Keep it simple: the smallest change that does the job. No abstraction, option or fallback the task didn't ask for.

## Commits

- Every commit message ends with `Committed-by: <who did the work>`, for example `Committed-by: Claude` or `Committed-by: Codex`.
- Never add a `Co-Authored-By` line or any other co-author trailer.
- One feature per commit.

## Checking a feature

- Check every feature with both Claude Code and Codex, on the Mac and on Workbox Test.
- Remote tests of the app run only on Workbox Test (SSH alias `workbox-test`).
- Real-window checks (the lag meter, memory) run on the Mac: pull the lane's branch into a Mac lane and run
  `shell/assembly.sh start --counters` there.

## Lanes

- Parallel work runs in lanes: `scripts/lane.sh new <name>` makes `~/dev/work/worktrees/Assembly/<name>` on branch
  `lane/<name>`, with its own ports, data folder, app id and Workbox Test host (`scripts/lane-env.sh`).
  `scripts/lane.sh remove <name>` tears it down; `scripts/lane.sh list` shows them. Main keeps the defaults.
- Lanes may run on the Mac or on the workbox (SSH alias `workbox`), each in its own lane folder. On the workbox,
  never touch the old Assembly host (`assembly-remote`), `tanstack-relay`, `tanstack-agent-server`, or Jesse's own
  sessions and data. Connect to no other machine.
- Builds share the machine: run at most 2 or 3 .NET builds at once across lanes, each with `MSBUILDDISABLENODEREUSE=1`.

## Safety

- Never open the old Assembly data folders: `~/Library/Application Support/dev.blackcolours.MacCommandBarWebviewPreview`
  and `~/.local/share/assembly`.
- Stop processes only by pid, and only processes you started.
