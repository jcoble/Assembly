<!-- intent-skills:start -->
## Skill Loading

Use the repository’s installed Intent. If it is unavailable, report the missing dependency instead of downloading a replacement.
Before editing files for a substantial task:
- Run `pnpm exec intent list` from `tauri-svelte-preview/` (the frontend package root) to see available local skills.
- If a listed skill matches the task, run `pnpm exec intent load <package>#<skill>` before changing files.
- Use the loaded `SKILL.md` guidance while making the change.
- Monorepos: when working across packages, run the skill check from `tauri-svelte-preview/` (the frontend package root) and prefer the local skill for the package being changed.
- Multiple matches: prefer the most specific local skill for the package or concern you are changing; load additional skills only when the task spans multiple packages or concerns.
<!-- intent-skills:end -->

## Assembly chat scope

TanStack guidance applies to chat-session work. Keep Assembly’s Rust backend, Tauri commands, channels/WebSockets, native session/context ownership and SQLite authority. For migration work, read `docs/tanstack-chat-implementation-plan.html` at the repository root. It proposes the next implementation; `main-architecture-explained.html` remains the current architecture. Do not extend the migration into the editor or other tabs. Read package source when examples and installed behavior differ.

## Async lifecycle

Every timer and promise chain in `src/` follows the async-lifecycle rule in the repository root
`AGENTS.md` / `CLAUDE.md` (owner, 2026-10-10): `async`/`await` with an `AbortSignal`, timers cleared by
their owner on destroy, and a backend cancel for real work. `pnpm test:async-lifecycle` enforces it.
