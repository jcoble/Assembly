[Notion](https://app.notion.com/p/409394b0689d8197a38efdfb8d400f84) is the source of truth for Assembly tasks, decisions and all documentation: plans, specs, recon, handoffs and artifact records. Keep task material on its Command Center task and reusable docs in this directory; proof assets live on Google Drive, linked from Notion and PRs.

# Assembly instructions

These project rules supplement the global instructions in `~/.codex/AGENTS.md`.

## Purpose and architecture

Assembly is an open-source desktop workbench: half IDE and half chat with AI coding agents, with orchestration, workflows, editor, terminal, Git and browser, using the user's provider subscriptions.

The desktop uses a C# backend and Rust GPUI frontend. The remote relay is a smaller, separate project limited to remote operations the desktop needs. Local/offline use must work; remotes are optional.

Assembly.Engine is the intended authority for operational state and transitions across sessions, workflows, the rail and Build Board; the UI consumes pushed state. Until the engine work lands, the designated runbook records live coordination state. The completed feature spec and reviewed implementation plan linked on the task govern the port. Follow their referenced `mac-command-bar` behavior and report gaps to the spawning parent.

Mobile is a first-class client. Include affected phone flows when changing shared sessions, relay, orchestration or workflows; verify them on a phone. See [Mobile documentation](https://app.notion.com/p/409394b0689d8163ab23f1907ffb7e88).

## Project requirements

- Fast and light: low CPU/RAM/GPU, responsive interaction and small messages. Cancel unneeded work, dispose of resources and keep idle features idle; animations end.
- Minimize timers. No repeated timer per rail/list row; a timer must serve a real need, have an owner and stop with it.
- Keep provider differences at adapter boundaries and shared behavior consistent across configured providers. Idle adapters are disposable; resume the provider session rather than keeping processes alive for switching.
- Node tooling and one-off scripts use TypeScript. Pin added dependency versions and keep lockfiles current. Use shared theme tokens for colors, spacing and sizes.
- Maintain daily packaging of upstream ACP adapter releases.

## Pull requests

Default to one complete feature per PR, with substeps and fixes kept as commits in that PR. Run focused checks while building, then one feature-level independent acceptance verification and code review. Recheck failed or affected behavior after fixes. Split only for independently shippable parts or a concrete size/risk that makes one PR unsafe to review; record the reason in the task's plan. Technical layers and implementation substeps alone do not justify separate PRs. Follow the global PR-autonomy and merge-permission rules.

## Locations and verification

| | Mac | Workbox |
| --- | --- | --- |
| Repo | `/Users/blackcolours/dev/work/Assembly` | `/home/blackcolours/dev/work/Assembly` |
| Worktrees | `/Users/blackcolours/dev/work/worktrees/Assembly/<task>` | `/home/blackcolours/dev/work/worktrees/Assembly/<task>` |

Use isolated app data and realistic seed data from a safe database backup. Never test against the owner's live data/services. Remote tests connect only to Workbox Test (`workbox-test`), never the owner's saved machines. Workbox `/tmp` is disk-backed; do not reconfigure mounts.

Cover four provider/host cases: Claude Code local, Codex local, Claude Code on Workbox Test, and Codex on Workbox Test. Use `scripts/check4.sh` for baseline checks on the completed feature build; report each result with evidence or its exact blocker.

The script does not drive the native UI. In the same acceptance verification, exercise the changed feature in the real native app with realistic data, covering affected provider/host and phone flows. Chat checks alone do not prove another feature; a browser preview is not native-app proof. Follow the global verification and Drive upload rules.
