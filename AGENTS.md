[Notion](https://app.notion.com/p/409394b0689d8197a38efdfb8d400f84) is the source of truth for Assembly tasks, decisions and all documentation. Nothing is documented in this repository. Task material (specs, plans, recon, handoffs) goes on its Command Center task, and reusable docs in that directory. Screenshots and videos go on Google Drive, linked from the task and the PR.

# Assembly instructions

These rules add to the global instructions in `~/.codex/AGENTS.md`. Before changing a file under `desktop/`, `dotnet/` or `scripts/`, read that folder's `AGENTS.md`.

## What Assembly is

Assembly is an open-source desktop workbench: half IDE and half chat with AI coding agents, with orchestration, workflows, editor, terminal, Git and browser, using the user's provider subscriptions.

- C# backend in `dotnet/`, Rust GPUI desktop in `desktop/`. The remote relay is a smaller, separate project limited to the remote operations the desktop needs. Local and offline use must work; remotes are optional.
- Assembly.Engine is the authority for the app's operational state and its transitions: sessions, workflows, the rail and the Build Board. The UI shows the state the backend pushes.
- The port follows the spec and reviewed plan linked on the task, and the `mac-command-bar` behavior they reference. Report gaps to your parent.
- The phone is a first-class client that has not been ported yet ([task](https://app.notion.com/p/41f394b0689d817780c8ccbeab5ac1ba)). Until it is, list the phone under what a PR did not cover.

## Rules for all code

- Read the [coding practices](https://app.notion.com/p/41e394b0689d812d8e77da536e185228) before writing code.
- Cancel what nobody needs: every async task, background job, timer and long-running operation has an owner that stops it.
- Fast and light: low CPU, RAM and GPU, responsive interaction, small messages. An idle feature uses nothing. Use spinners and repeating animations sparingly: one spinner can push GPUI to 80-100% CPU.
- Provider differences stay at the adapter boundary, and shared behavior is the same across providers. Adapters are stock: never patch one. Idle adapters are disposable: resume the provider session instead of keeping a process alive.
- No API keys for any provider. Assembly uses the user's own sign-ins.
- Maintain daily packaging of upstream ACP adapter releases.

## Sessions and lanes

- One owner per feature until it is done. Never run a clone or duplicate of a session: check what is running and resume it.
- Keep your open work on your task list. When an item is done or waiting on someone, take the next. When the list is empty, tell your parent. Commit and push often; only PRs wait.
- The run's runbook holds the agents' coordination state: owners, next step, blockers. It is not app state. It stays within 6,500 characters.
- [Which model does which work](https://app.notion.com/p/41f394b0689d81908bbceecaab2600a9).

## Tests, pull requests and verification

- A test is written only when it is one of three kinds: something the app cannot show by use (concurrency, races, cancellation, restarts, dropped connections, large data, hard edge cases); a bug, where the fix starts with a test that fails on it; or one of a few highest-level tests of what the user gets from a usable feature (the socket, or the panel against a scripted backend). Labels, wording, layout, constants, table contents and the shape of the code are not tested, and no pinning test is written until Jesse calls the feature done ([coding practices, Tests](https://app.notion.com/p/41e394b0689d812d8e77da536e185228)).
- This overrides the test-per-behavior and red-green-refactor defaults of the `tdd`, `solid` and `implement-spec` skills. A spec's "Testing Decisions" section names the tests its feature gets; ask your parent before adding another.
- A PR lists the tests it adds, one reason each from the three kinds, so the reviewer can refuse the rest.
- While building, run only the tests for what you changed. The whole suites run on GitHub: a ready PR and main run them, and for a draft you start them with `gh workflow run ci.yml --ref <branch>`.
- One complete feature per PR, with substeps and fixes as commits in it. Split only for parts that ship independently or a size that is unsafe to review, and record the reason in the plan.
- In the PR description, name the providers your change affects, with one line on why, and write one rough paragraph on the flow through the change as a user would meet it. That paragraph is an estimate of the path, not a step-by-step guide.
- A ready PR gets one Codex review and one independent check in the real window by a different model. To check a PR or brief its verifier, load the `orient-verification` skill. Fix what they find and recheck only what the fix touched. A second review round is not required, but a real finding from one is still fixed.
- Check back on your PR for review comments and the verifier's result.
- Who may OK: Jesse for locked paths (`scripts/locked-paths.txt`), which hold the pinned tests. Merges to main follow the global rules.

## Where things run

| | Mac | Workbox |
| --- | --- | --- |
| Repo | `/Users/blackcolours/dev/work/Assembly` | `/home/blackcolours/dev/work/Assembly` |
| Worktrees | `/Users/blackcolours/dev/work/worktrees/Assembly/<task>` | `/home/blackcolours/dev/work/worktrees/Assembly/<task>` |

Use isolated app data with realistic seed data. Never test against the owner's live data or services. Remote tests connect only to Workbox Test (`workbox-test`), never the owner's saved machines. Workbox `/tmp` is disk-backed; do not reconfigure mounts.

## Agent skills

These replace the `docs/agents/*.md` files the engineering skills look for:

- Issue tracker: Notion Command Center Tasks, Project = Assembly. See [Agent skills: issue tracker](https://app.notion.com/p/41f394b0689d815e9507d846a920f0a4).
- Triage labels: the Tasks Triage select, default five roles. See [Agent skills: triage labels](https://app.notion.com/p/41f394b0689d815d9d54c38f04e065c8).
- Domain docs: single-context; glossary and ADRs live in the Notion Library, not the repo. See [Agent skills: domain docs](https://app.notion.com/p/41f394b0689d8197bd1ee2be4865f58c).
