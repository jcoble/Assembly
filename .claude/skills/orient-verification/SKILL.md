---
name: orient-verification
description: How an Assembly PR is checked in the real window on the Workbox. Use when you verify a PR, or when you write a verifier's brief.
---

# Orient: verification

The rule's source is [Verification process, "Current rule"](https://app.notion.com/p/41f394b0689d81e0b83ad6b7798db7eb). Read it when this short form leaves a question open; where the two differ, the page wins.

## The rule

- **Providers.** The implementer names the affected providers in the PR, with one line on why. The verifier reads the diff and confirms that list or widens it.
  - Inside one provider's own code: check that provider only, on this machine and on a remote.
  - In shared session, chat or engine code: check one provider fully, plus one message and one tool call on each other provider.
  - Neither: no provider check, only the feature.
  - A provider on the list that is signed out is a blocker. The check is done only once it is signed in and run.
- **Who.** The verifier is a different model from the implementer, and it works on the Workbox.
- **What.** While the build runs, read the spec, the PR's flow paragraph and the diff. In the real window, check the happy paths and some easy edge cases. Trying hard to break the change is later deep testing, not this check.
- **Green.** A PR is not green until this check has passed.

## Writing a verifier's brief

The brief names this skill instead of repeating it. It carries only what changes per PR: the PR and its head SHA, the task link, the lane's name and worktree, and the implementer's provider list and flow paragraph. Keep the check to about five minutes, one or two screenshots and at most one video of 120 seconds or less.

## Checking a PR on the Workbox

Work in a lane worktree, never in Jesse's checkout. Track and stop only the processes you start.

1. **Checkout.** `scripts/lane.sh new <name>` (or `adopt <name>`) makes a worktree with a `.lane` file: its own ports, data folder, app id and Workbox Test host. `scripts/lane-env.sh` stops any checkout off `main` that has no `.lane`. Check out the PR's head SHA in it.
2. **Build** only what changed, one build at a time, after `free -g`. The commands are in the header of `scripts/ui-check.sh`: `cargo build` into `$DATA/target` and `dotnet publish` of `dotnet/Assembly.Desktop` into `$DATA/ui-check/backend`.
3. **Seed data.** `npm run seed:dev` resets this worktree's own profile to synthetic data: sessions on every provider, projects with small Git repos, workflows and Board questions. It reads no live data. Export the profile it prints as `ASSEMBLY_UI_CHECK_PROFILE`.
4. **Remote host.** `dotnet/scripts/workbox.sh serve` starts this lane's backend on Workbox Test. `node scripts/check-remote.ts <lane name> <lane number>` confirms it matches your build. Connect only to Workbox Test (`workbox-test`), never to Jesse's saved machines.
5. **Run the window.** `scripts/ui-check.sh start` opens the app on a private Xvfb display and prints `DISPLAY`. It refuses to start with 6 GB or less free. The window sits at 0,0 at 1710x990, so `xdotool` screen positions match screenshot pixels. Take screenshots with `scripts/ui-check.sh shot <path>`.
6. **Stop.** `scripts/ui-check.sh stop` stops exactly what `start` started. `dotnet/scripts/workbox.sh stop` stops the lane's host.
7. **Proof.** Upload each screenshot and video to Google Drive with `~/.local/bin/evidence add <file> --task TSK-<id> --desc "<what it proves>"`, following [Screenshots and videos for a PR](https://app.notion.com/p/409394b0689d81fe90e1d30f4b7b06cc). Link each file, not only the folder, from the task and the PR, and confirm each link opens.
8. **Report.** The tested SHA; the provider list, confirmed or widened; per behavior, pass or fail with what you did and what you saw; anything blocked, with its exact error; and what you did not cover, including the phone.
