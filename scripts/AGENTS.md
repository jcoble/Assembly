# scripts/: tooling, hooks and lanes

These rules add to the root `AGENTS.md`.

- Node tooling and one-off scripts are TypeScript, run by `node` directly. `lint.d/00-typescript-only.sh` refuses `.js`, `.mjs` and `.cjs` files, and the pre-commit hook refuses new or changed Python.
- Pin an added dependency to an exact version and commit the updated `pnpm-lock.yaml`.
- Tests sit beside the script as `<name>.test.ts` or `<name>.test.sh`. Run one with `node --test scripts/<name>.test.ts` or `bash scripts/<name>.test.sh`.
- The hooks are in `.githooks/` (`scripts/lane.sh` sets `core.hooksPath` for a lane):
  - `commit-msg` runs `check-commit-msg.sh`: no co-author line, an `Architecture:` line, a `Task:` line on a `tsk-<n>` branch, `Committed-by:` last, a `fix` commit changes a test or says `No-test: <reason>`, and a commit that changes or removes an assertion in an existing test has a `TEST-CHANGE: <test>: <reason>` line for it (`hooks/test-integrity.ts`). No signature is needed. The CI `commits` job lists every such line.
  - `pre-commit` checks locked paths (`check-pinned.sh`), runs `lint.sh` and `hooks/staged-policy.ts`.
  - `pre-push` checks each outgoing commit with `check-commit-msg.sh --commit`.
- Locked paths are listed in `locked-paths.txt`. A change to one needs an OK line in `.pinned-approvals`, printed by `scripts/check-pinned.sh approve <path> jesse`. The root `AGENTS.md` says who may OK it. The pinned tests are locked paths, so this is the only approval a test change can need; `scripts/pinned-guard.sh` stops the Claude Code edit without it.
- A guard or hook that refuses something is the answer. Quote its message to your parent and carry on with what is not blocked; never commit with `--no-verify`.
