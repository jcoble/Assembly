# How Assembly is put together

The short version of the design. The full reasoning is in `docs/plans/2026-10-26/implementation-plan.md` (section 4
for the layout, section 5 for the socket); the rules are in `AGENTS.md`. A commit that changes anything described
here updates this file in the same commit. Every other commit says `Architecture: unchanged` in its message
(`scripts/check-commit-msg.sh` checks it).

## The pieces

- `shell/`: the Tauri window (Rust). It starts the Mac's backend and hands the page its port and token
  (`window.__ASSEMBLY__`).
- `app/`: the page (SvelteKit). Code is one folder per feature, `app/src/lib/<feature>/`. Only `app/src/lib/host/`
  talks to Tauri or adds window, document or media-query listeners.
- `dotnet/`: the backend (.NET). The same backend runs on the Mac and, headless, on every remote machine.

## The backend: a core in layers, the rest by feature

The core follows Clean Architecture, with each layer pointing inward. `ArchitectureTests` checks the rules.

- `Assembly.Domain/`: only the records several features share (agent events, rows, sessions, markdown blocks). It
  references nothing.
- `Assembly.Application/`: references Domain only, with no SQLite, ASP.NET Core or process starting.
  - `Common/`: the contracts every feature uses (database, agents, machines) and the ACP protocol code.
    `Common/Acp/V1/` is the one place that knows ACP's shapes and each provider's quirks.
  - `Features/<Feature>/`: everything one feature is: its records, its rules, its tables and its plain SQL. A
    feature never uses another feature's folder. There's no repository: a feature reads through `IAppDatabase` and
    writes through `IAtomicWriter`.
- `Assembly.Infrastructure/`: references Application. SQLite and the one writer, starting agent processes and git,
  the local and remote machines, and the files and native calls behind a feature (`Infrastructure/<Feature>/`).

## The hosts

- `Assembly.Mac/`: the Mac's backend, started by the shell.
- `Assembly.Remote/`: the backend on a machine that runs agents (`serve`), plus its test benches.

A host's `Program.cs` only reads its arguments, registers the layers and maps its endpoints.

## One socket

The page talks to the backend over one WebSocket, through one file, `app/src/lib/socket.ts` (plan step B4). Until
that lands, `app/src/lib/call.ts` and `Assembly.Infrastructure/Http/` are the one HTTP path, and the `/live` socket
carries the rows. The only other HTTP is `/mcp`, which the agents use.

## Where a new feature goes

- Backend: `dotnet/Assembly.Application/Features/<Feature>/`. If it needs an outside system (git, files, a
  process), the contract stays in the feature folder and the implementation goes in
  `dotnet/Assembly.Infrastructure/<Feature>/`.
- Tests: `dotnet/Assembly.Tests/Features/<Feature>/` and `app/test/`.
- Page: `app/src/lib/<feature>/`.
- A record or contract two features need moves to the core: a record to Domain, a contract to `Common/`.
- No folder holds more than 30 files (`ArchitectureTests`), and no source file more than 600 lines
  (`scripts/lint.d/20-file-size.sh`).

## How changes land

Agent work always lands on main through a pull request; nothing is pushed to main directly. CI builds and tests
every push and pull request, and checks each pull request's own commits with `scripts/check-commit-msg.sh`.
