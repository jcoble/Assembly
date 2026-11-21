# Workbox Test for Assembly

Adapted from mac-command-bar's `tools/workbox-test/README.md` at commit `682a1ca9`.
Remote tests use only the existing Workbox Test host, SSH alias `workbox-test`, never the owner's saved machines.
Assembly's backend is .NET; do not install or reset the old Rust backend to test it.

## What Assembly has

- `dotnet/scripts/workbox.sh serve [branch]` starts this lane's backend on Workbox Test.
  `stop` stops its systemd user unit. Read the script before running it: `serve` transfers a checkout and builds.
- `scripts/lane-env.sh` sets the lane's `BOX_PORT`, `BOX_DATA`, `BOX_TOKEN_FILE` and `BOX_UNIT`.
  Main uses port 9797 and `assembly-dotnet`; lanes use their own suffixed folders and units.
  Never point a test lane at main's host or data.
- `scripts/lane.sh` reserves lane numbers on Workbox Test. Create/adopt/remove through that script when authorized;
  removing a lane removes its host and checkout too. Report dirty worktrees instead of deleting them.
- `scripts/check4.sh` reaches the host through the local backend's tunnel and checks the served commit.
  See [four-scenario check](four-scenario-check.md).

## Container setup that still needs porting

The source README describes container `assembly-test`, image `assembly-test`, named volume `assembly-test-home`,
Ubuntu 24.04 with systemd, and SSH on the workbox's Tailscale address at port 2223. These are source-checkout
facts, not a live verification of the host. The volume holds logins, installed tools and data; preserve it.

Assembly does not contain `tools/workbox-test/Dockerfile` or the container entrypoint. Those files need porting
before Assembly can document its own rebuild procedure. This document does not provide a build command referring
to missing files. Use the provisioned host; if it is unavailable, report the exact SSH/setup error.

The source's port 7777, `assembly-remote.service`, `~/.local/share/assembly` reset command and normal
"Install and connect" flow describe the old app. They are not the .NET lane deployment procedure. Use the lane's
`dotnet/scripts/workbox.sh` and settings above. Never run the source's reset or volume-wipe commands here.

## Provider accounts and shells

The source documents owner sign-in for Claude Code, Codex and Antigravity inside the container. Verify the
providers required by the scenario are signed in there; do not treat the Mac's login as the remote login.
Never sign out to simulate failure. If sign-in is needed, report it to the owner.

The source's `docs/remote-terminal.md` describes shells owned by its Rust backend, which end when that backend
restarts. Assembly's terminal port is planned in W4 of `docs/plans/2026-10-26/implementation-plan.md`;
that lifecycle and those wire commands are not implemented by this document. Do not restart the owner's old
backend or its Terminal while working on an Assembly lane.
