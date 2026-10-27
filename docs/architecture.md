# How Assembly is put together

The short version of the design. The full reasoning is in `docs/plans/2026-10-26/implementation-plan.md` (section 4
for the layout, section 5 for the socket); the rules are in `AGENTS.md`. A commit that changes anything described
here updates this file in the same commit. Every other commit says `Architecture: unchanged` in its message
(`scripts/check-commit-msg.sh` checks it).

## The pieces

- `shell/`: the Tauri window (Rust). It starts the desktop backend and hands the page its port and token
  (`window.__ASSEMBLY__`).
- `app/`: the page (SvelteKit). Code is one folder per feature, `app/src/lib/<feature>/`. Only `app/src/lib/host/`
  talks to Tauri or adds window, document or media-query listeners.
- `desktop/`: the window in Rust on GPUI Kit, which replaces `shell/` and `app/` in G0 of the GPUI switch plan. A
  small `src/core/` (with the one socket client in `core/socket/`) and one folder per feature in `src/features/`;
  `desktop/scripts/guard.sh` holds it to that.
- `dotnet/`: the backend (.NET). The same backend runs on the desktop and, headless, on every remote machine.

## The backend: a core in layers, the rest by feature

The core follows Clean Architecture, with each layer pointing inward. `ArchitectureTests` checks the rules.

- `Assembly.Domain/`: only the records several features share (agent events, rows, sessions, markdown blocks). It
  references nothing.
- `Assembly.Application/`: references Domain only, with no SQLite, ASP.NET Core or process starting.
  - `Common/`: the contracts every feature uses (database, agents, machines) and the ACP protocol code.
    `Common/Acp/V1/` is the one place that knows ACP's shapes and each provider's quirks.
  - `Features/<Feature>/`: everything one feature is: its records, its rules, its tables and its plain SQL. A
    feature never uses another feature's folder. There's no repository: a feature reads through `IAppDatabase` and
    writes through `IAtomicWriter`. Each write is one transaction with its audit row, and the changes it publishes
    (`IWriteScope.Publish`) go to `ITopics` only after it commits.
- `Assembly.Infrastructure/`: references Application. SQLite and the one writer, starting agent processes and git,
  the local and remote machines, and the files and native calls behind a feature (`Infrastructure/<Feature>/`).

## The hosts

- `Assembly.Desktop/`: the desktop backend, started by the shell.
- `Assembly.Remote/`: the backend on a machine that runs agents (`serve`), plus its test benches.

A host's `Program.cs` only reads its arguments, registers the layers and maps its endpoints.

## One socket

The page talks to the backend over one WebSocket, `/socket`, through one file, `app/src/lib/socket.ts`. The backend's
side is `Assembly.Infrastructure/Socket/`: a feature registers its calls, saves and topics on `ICallRegistry`
(`Assembly.Application/Common/Calls/`), and `SocketDispatcher` runs every call the same way (plan 5.2). A write's
changes reach subscribers through the host's one `Topics` (`Assembly.Infrastructure/Topics/`), which the writer
publishes to after it commits (plan 5.7). A save is one write per change, and its changes go out on the topic named
after its feature, by key, which is how `socket.ts` holds a key while the window's own saves of it are in flight
(plan 5.9). A host hands its features to `ApiServer` in `ApiServices.Features`, which registers them on the socket;
the desktop's first is preferences (B7). The socket exists from plan step B4, and features move onto it from B7; the
page still uses `app/src/lib/call.ts` and `Assembly.Infrastructure/Http/`, their HTTP path, until its own switch, and
the `/live` socket carries the rows. The only other HTTP is
`/mcp`, which the agents use.

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
