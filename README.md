# Assembly

A desktop app for running coding agents, Claude Code and Codex, on this computer and on remote machines.

- `app/`: the UI (SvelteKit).
- `dotnet/`: the backend and the window (.NET).

The rules for working in this repository are in [AGENTS.md](AGENTS.md).

## Run the desktop app

Run `pnpm app:dev` from the repository root or `app/`. It builds the .NET backend and launches the native GPUI
window in `desktop/`, not the old Tauri/Svelte window. Requires Node.js with TypeScript stripping, pnpm, the .NET
SDK and the Rust toolchain pinned in `desktop/rust-toolchain.toml`.

It works from any checkout with nothing to set up. Main uses `~/assembly-dotnet/mac`, a lane worktree keeps its lane's
data and ports, and any other checkout gets its own data under `~/assembly-dev/worktrees/` and its own ports. The
desktop owns the backend and stops it when the app closes.
`ADAPTERS` overrides the ACP adapter installation folder (default `adapters` beside that data).
