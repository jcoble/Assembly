# Four-scenario check in Assembly

Adapted from mac-command-bar's `docs/operations/four-scenario-check.md` at commit `682a1ca9`.
Assembly uses `scripts/check4.sh` for automated checks and a separate native-window feature check.
A browser preview is not proof of the app. Use real-looking test data with long names, many projects and
worktrees, and large chats. Several changes can share a run only when it exercises each change and satisfies
Assembly's commit-evidence rule.

## Setup and isolation

Use this checkout's lane, with its `.lane` settings and `scripts/lane-env.sh`. Each lane has separate ports,
data, app identity and Workbox Test host. Never run two test apps for the same lane or stop another lane's app.
The old `fourScenarios.ts` start/stop runner and its canonical-path hash are not in Assembly.

- On agent-workbox, run `free -g` before a build or launch; wait if available memory is 6 GB or less.
  Run only one heavy job per lane. Never put large build output in its RAM-backed `/tmp`.
- Use only Workbox Test (`workbox-test`) for remote scenarios. See [Workbox Test](workbox-test.md).
- Never open the owner's data folders. Copy approved test fixtures into the lane's data folder; never symlink.
  Data flows into that copy only. Remove other saved machines and cached remote sessions before launch.
  Assembly has no equivalent of the old runner's seed-and-sanitize command. Do not run its SQLite deletion
  statements here: Assembly has a different schema. If safe, realistic fixtures are unavailable, report blocked.
- Never sign out of a provider to test sign-in. Provider logout can revoke the login; restoring a file may not
  restore access. Use an owner-approved test login.

## Automated check

Read `scripts/check4.sh` and `scripts/features/README.md` before running. Both the local backend and this lane's
Workbox Test host must run the commit being checked. `dotnet/scripts/workbox.sh serve` starts that host;
`dotnet/scripts/desktop.sh` is the headless local backend runner. These scripts build and start processes; they
are not inspection commands. The checkout must be clean apart from generated evidence.

Run `scripts/check4.sh` from the worktree. It checks one turn for each provider and machine, plus every feature
scenario in `scripts/features/`. `scripts/check4.sh --feature <name>` is for a quick partial run and is not evidence.
The full run writes `evidence/<commit>/four-way/summary.json`, `summary.md`, rows and scenario logs. Commit that
folder with the feature; never edit evidence by hand. CI checks it with `scripts/check-evidence.sh`.

Report these four rows with links to the generated evidence. If a case cannot run, report blocked with the exact
error; never substitute another provider, machine, or result.

| Provider | Machine | Result | Evidence or exact blocker |
|---|---|---|---|
| Claude Code | local | | |
| Codex | local | | |
| Claude Code | Workbox Test | | |
| Codex | Workbox Test | | |

## Native feature check

The automated turn check does not prove the window works. Open the changed feature in the real native app,
with realistic test data; use it as the task describes and inspect for cut-off, repeated or empty content.
For chat/history changes, also check both providers on both machines: send a prompt that produces substantial
output, send a second prompt, quit, reopen and check order and duplicates. For remote history, check reopening
without a previously cached session, viewing it offline, then reconnecting and sending another message.
Record each exercised action and its result; an unrun action stays blocked, with its exact error.

Run it in the native GPUI app on agent-workbox, from the lane's worktree: `scripts/ui-check.sh start` builds and
starts the app and its backend on a private Xvfb display at 1710x990, `scripts/ui-check.sh shot <file>` saves a
screenshot, and `scripts/ui-check.sh stop` stops only what `start` started. Drive it as AGENTS.md "Checking a
feature" says (`assembly-native-ui` and `xdotool`, or Codex's computer use). If `start` fails it prints the failing
step; report that as blocked. Never substitute the old Tauri shell (`shell/assembly.sh`) or a browser for the window.

## Screenshots, publishing and cleanup

Save before/after files as `~/Workbox/screenshots/TSK-<id>-<what>-<before|after>.png`. On agent-workbox, from the
worktree, run `evidence add <file> --task TSK-<id> --desc "<what it proves>"`, then `evidence publish TSK-<id>`.
Link the Drive proof from the task and PR, including a "Feature check" line. See [publishing steps](../evidence.md)
for the external tool and screenshot/video limits. Never commit screenshots.
`scripts/check4.sh window <commit> <file>...` records screenshot names in the generated summary.

Close each browser and app you opened as soon as its check ends, including after a failed start. Verify that its
process tree exited. Stop only the PIDs or process groups you recorded at startup. The old runner's `stop` command
is not available here. `dotnet/scripts/workbox.sh stop` stops only the configured lane host;
`shell/assembly.sh stop` is for that lane's old Tauri shell. Neither is a GPUI window teardown command.
