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
  small `src/core/` (with the one socket client in `core/socket/`, the one owner of
  native views in `core/native_views/`, the backend launcher in `core/backend/`, the palette's command list in
  `core/commands/`) and one folder per
  feature in `src/features/`; `desktop/scripts/guard.sh` holds it to that. The Terminal tab, `features/terminal/`,
  keeps an `alacritty_terminal` grid fed by the backend's `terminal` topic and draws it with GPUI text.
- `dotnet/`: the backend (.NET). The same backend runs on the desktop and, headless, on every remote machine; both hosts start the engine runtime.

## The backend: a core in layers, the rest by feature

The core follows Clean Architecture, with each layer pointing inward. `ArchitectureTests` checks the rules.

- `Assembly.Domain/`: only the records several features share (agent events, rows, sessions, markdown blocks). It
  references nothing.
- `Assembly.Application/`: references Domain only, with no SQLite, ASP.NET Core or process starting.
  - `Common/`: the contracts every feature uses (database, agents, machines) and the ACP protocol code.
    `Common/Acp/V1/` is the one place that knows ACP's shapes and each provider's quirks.
  - `Features/<Feature>/`: everything one feature is: its records, its rules, its tables and its plain SQL. A
    feature never uses another feature's folder. There's no repository: a feature reads through `IAppDatabase`
    (reads needing one consistent view use `IAppDatabase.OpenSnapshot`) and writes through `IAtomicWriter`.
    Each write is one transaction with its audit row, and the changes it publishes (`IWriteScope.Publish`) go to `ITopics` only after it commits.
- `Assembly.Engine/`: references Application for existing database contracts. Owns operational commands, transitions,
  reads, prefixed `engine_` tables, committed-event publishing and durable outbox execution. Writes go through
  `IAtomicWriter`; the engine has no SQLite, process, filesystem or transport implementation.
  [Engine design §17.3](https://app.notion.com/p/40a394b0689d81299136fb94fcf3ccf1) governs this boundary.
- `Assembly.Infrastructure/`: references Application and Engine. SQLite and the one writer, starting agent processes and git,
  the local and remote machines, and the files and native calls behind a feature (`Infrastructure/<Feature>/`).
  `Infrastructure/Engine/` composes the engine runtime with the host topics and owns its window calls and socket transport.
  A session's folder on disk is `SessionStore.Root` (on its `sessions` item as `root`); anything that reads inside it
  turns a relative path into a full one with `Infrastructure/Chat/SessionPaths`, which refuses paths outside it.
  `Infrastructure/Chat/Files/` lists, searches and reads it for the window's Files and Search tabs and its read-only file tabs.

## The hosts

- `Assembly.Desktop/`: the desktop backend, started by the shell.
- `Assembly.Remote/`: the backend on a machine that runs agents (`serve`), plus its test benches.

A host's `Program.cs` only reads its arguments, registers the layers and maps its endpoints.

## One socket

The authenticated `/socket` also carries the engine's six filtered topics and local display-only `mirror` topic through `stream:"engine"`, with cursor fingerprints, client-pulled mirror snapshots, catch-up pages and reader acknowledgements; `engine/machine` reports the local identity and watermark, and omitting `stream` preserves the existing topics.
Each active remote has one `EngineMirror` on that same socket. Socket-open events restart its identity check and
subscription; it has no timer. `MirrorApply` copies remote display rows and journal events through the existing
atomic writer, hiding partial snapshot generations until promotion. Mirror application never transitions local
entities or queues effects; acknowledgements and page pulls follow committed writes.
The window can select a machine on an engine subscription or entity read. Local reads keep their existing path;
foreign reads use only the mirror's current generation and cursor. One lazy publisher per selected mirror shares
its runtime's existing wake loop, and generation promotion pushes a fresh snapshot to its active subscriptions.
Publisher drains bind the captured generation and fence publication across resets. Streams tag queued work with the
reset version, prioritize fresh snapshots and fence replay and cursor updates across promotion.
Mirror retention uses the same atomic writer, keeps the newest 10,000 events and respects that mirror's window
acknowledgements. Runtime shutdown cancels the streams and removes its single mirrored-change subscription.
The window forwards commands and unmirrored tool/usage reads to the owning saved machine over its existing live/bulk
sockets. Both ends enforce the window command allowlist. Forwarded commands keep their identity, version and epoch,
use a separate write-order key per machine, and return the owner's receipt without changing local engine state.

`/socket/terminal` is the third socket per machine, carrying only terminal feature calls and the terminal topic.
For remote sessions, `RoutedTerminals` uses the terminal socket owned by `RemoteMachine` through its existing tunnel;
output is relayed onto the local terminal topic. The [TSK-1639 task](https://app.notion.com/p/408394b0689d8122911ae831ac180542)
holds the terminal routing decisions and verification.

The page talks to the backend over one WebSocket, `/socket`, through one file, `app/src/lib/socket.ts`. The backend's
side is `Assembly.Infrastructure/Socket/`: a feature registers its calls, saves and topics on `ICallRegistry`
(`Assembly.Application/Common/Calls/`), and `SocketDispatcher` runs every call the same way (plan 5.2). A write's
changes reach subscribers through the host's one `Topics` (`Assembly.Infrastructure/Topics/`), which the writer
publishes to after it commits (plan 5.7). A save is one write per change, and its changes go out on the topic named
after its feature, by key, which is how `socket.ts` holds a key while the window's own saves of it are in flight
(plan 5.9). A host hands its features to `ApiServer` in `ApiServices.Features`, which registers them on the socket;
the desktop's first is preferences (B7). The socket exists from plan step B4, and features move onto it from B7; the
page still uses `app/src/lib/call.ts` and `Assembly.Infrastructure/Http/`, their HTTP path, until its own switch, and
the `/live` socket carries the page's rows. A remote session's rows reach the desktop over the machine socket instead
(the `rows` topic and the bounded `rows/after` read on the remote's `/socket`), reopened on the backend clock when it
drops. The machine socket also carries agent listing, session and helper creation, sends, run control and config
calls. Its welcome checks the row format before the machine is reachable, and its `sessions` topic pushes titles
and activity into the desktop's session store and summaries. The only other HTTP is `/mcp`, which the agents use.

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
