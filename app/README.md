# MacCommandBar Tauri/SvelteKit Preview

The Assembly desktop app. The folder keeps its early prototype name; see the [root README](../README.md) for what the app does and how to build it.

The SvelteKit app follows Tauri's current SvelteKit guidance: static adapter, SPA mode, `build/` output, and a Tauri `devUrl` pointed at Vite.

## Run

```bash
pnpm install
pnpm dev
```

Open `http://127.0.0.1:5177` for the browser preview.

For the Tauri shell:

```bash
pnpm tauri:dev
```

If Vite is already running on `127.0.0.1:5177`, use attach mode so Tauri does
not try to start a second dev server:

```bash
pnpm tauri:dev:attach
```

The browser preview uses the local Vite source bridge for filesystem-backed source scans, reads,
writes, and basic search. The Tauri shell uses native Rust commands for the same source operations
plus native Git, LSP, terminal, worktree, process, and session actions.

## Appearance

Source preview font and theme settings live in `src/lib/sourcePreviewAppearance.ts`.

## Verify

```bash
pnpm check
pnpm build
cargo check --manifest-path src-tauri/Cargo.toml
pnpm test:native-lsp
```

The source editor is CodeMirror 6. Language servers start only when Supercharged is on.

`pnpm test:native-lsp` exercises the Rust LSP bridge directly. It uses TypeScript and C# scratch workspaces and verifies symbols, hover, definition, references, and diagnostics against the same registry used by the Tauri commands.

## Orchestration Events

Long-running agent loops can append visual run events through the repo-level
bridge:

```bash
../scripts/mcb-orch --run-id run-tsk-127 --preset scenario-started --scenario "Trading partner setup"
../scripts/mcb-orch --run-id run-tsk-127 --preset issue-found --issue-id AUTH-7 --issue-count 1
../scripts/mcb-orch --run-id run-tsk-127 --preset batch-delegated --agent-role fix-agent --fix-count 2
../scripts/mcb-orch --run-id run-tsk-127 --preset ui-verified --scenario "Trading partner setup" --verified-count 1
```

The Source Browser command palette can copy ready-to-edit versions of those
commands for the selected project: run-started, scenario-started, issue-found,
fix-batch, UI-verified, and approval-required.
