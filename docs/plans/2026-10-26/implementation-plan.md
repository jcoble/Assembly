# Assembly: implementation plan

Written 26 October 2026 by Claude Code on agent-workbox, at lane/base `9e51121`, for Jesse to review. It replaces
the plan in `docs/# Assembly .md` (Sol's), which stays in the repo unchanged as the long list of features to port.
This plan is the part that list was missing: the base every feature is built on, spelled out file by file, so that
each later feature has one obvious way to be written and nothing to invent.

Nothing in this plan is built yet. No code and no locked file changed to write it. The measurements in sections 1, 5.6 and 5.9
were run on agent-workbox in a scratch folder, from packages already in the local NuGet cache.

How to read it:

- **Section 1** has the four decisions. Jesse made all four on 2026-10-26 at 19:27Z; the facts and options are kept
  for the record.
- **Section 2** says where each of Jesse's comments in Sol's doc went.
- **Sections 3 to 9** are the base: the rules, the solution layout, the shared plumbing, every timer in the page today
  and where it goes, the promise chains, and the guards and lint.
- **Section 10** is the base's commits, in order. Each has its files, the failing test written first, the proof, and a
  rough size.
- **Section 11** is the later phases. They're shorter, but each still has its files, tests and proof.
- **Section 12** lists what this plan doesn't settle.

## 1. Decisions (all four made by Jesse, 2026-10-26 19:27Z)

### D1. Database access: EF Core `DbContext`, or plain `Microsoft.Data.Sqlite`

Jesse wants `DbContext` used directly, as in the codewithmukesh article, instead of a repository layer. The backend
also ships as Native AOT today: `shell/assembly.sh:29` publishes `Assembly.Mac` with `-p:PublishAot=true`, and every
project has `IsAotCompatible=true` and `TreatWarningsAsErrors=true` (`dotnet/Directory.Build.props`). Those two goals
pull in opposite directions.

**What Microsoft says.** The EF Core page on Native AOT (updated June 2026) is titled "NativeAOT Support and
Precompiled Queries (Experimental)". It warns: "NativeAOT and query precompilation are highly experimental feature,
and are not yet suited for production use... recommend against deploying EF NativeAOT applications in production."
Other limits it lists:

- Queries built up in pieces (a `Where` added only when a condition is true) aren't supported.
- The generated code has to be regenerated after any change to the model or a query, with
  `dotnet ef dbcontext optimize --precompile-queries --nativeaot`.
- Publishing reports trimming and AOT warnings.

**What I measured.** The test was one tiny program, written twice: open a database, insert 1,000 rows in one
transaction, then count with a filter. One version uses EF Core 10.0.1 with SQLite, the other `Microsoft.Data.Sqlite`
10.0.0. Each ran three times on agent-workbox (linux-x64), and the numbers are the peak memory the process reported
(`VmHWM`). This is a small test, not the app; it shows the relative cost of the library, not what the backend will use.

| Build | Peak memory | Size | Notes |
| --- | --- | --- | --- |
| `Microsoft.Data.Sqlite`, JIT | 38 MB | 32 MB folder | |
| EF Core, JIT | 83 MB | 37 MB folder | Works. |
| `Microsoft.Data.Sqlite`, Native AOT | 7.3 MB | 2.8 MB binary | Publishes with 0 warnings. |
| EF Core, Native AOT, plain | — | 17 MB binary | 15 trim/AOT warnings. Crashes at start: "Model building is not supported when publishing with NativeAOT. Use a compiled model." |
| EF Core, Native AOT, compiled model and precompiled queries | 34.6 MB | 17.2 MB binary | Works, after `dotnet ef dbcontext optimize`. 17 warnings (IL2026, IL2037, IL2104, IL3002, IL3050, IL3053, CS9270), so it fails our TreatWarningsAsErrors unless those are turned off for it. `EnsureCreated` crashes ("Design-time DbContext operations are not supported"), so the schema has to be made with raw SQL. |

The options:

- **A. EF Core, JIT.** Drop Native AOT for the backend and use `DbContext` exactly as the article does: an
  `IApplicationDbContext` in Application, the concrete context in Infrastructure, LINQ, and migrations. This costs
  memory: in this test, 83 MB against 7 MB.
- **B. EF Core, Native AOT, experimental.** Keep AOT, and use a compiled model with precompiled queries. That means
  turning off warnings-as-errors for those warnings, regenerating after every model or query change, writing no
  queries built in pieces, and making the schema with SQL rather than migrations. Microsoft says not to ship this.
- **C. `Microsoft.Data.Sqlite`, Native AOT.** Keep today's library and AOT. Application gets one small
  `IAppDatabase` interface for reads and one `IAtomicWriter` for writes (section 5.6). That isn't a repository per
  entity: feature code writes its own SQL against one connection inside one transaction. It's the smallest and the
  lightest, but it's not the `DbContext` style Jesse prefers.

**Decided by Jesse, 2026-10-26 19:27Z (card): C,** plain `Microsoft.Data.Sqlite` with Native AOT and the shared
`IAtomicWriter`. In his words: "You don't need EF Core, i'd rather plain SQL instead anyways, I can read it better
than ef core." The rest of the plan is written for C only.

### D2. The lag meter, the counters sampler and the soak

These three measure the page. Today they use timers (section 7): the lag meter needs a callback on each frame the
page draws (`requestAnimationFrame`) to measure frame gaps and key-to-paint delay, and the soak waits between the
steps it drives. Zero timers in `app/src` means they can't stay where they are. The options:

- **A. Measure tools outside `app/src`.** Move them to `app/measure/` (TypeScript), which has its own guard rules
  (below). The
  shell injects them into the page only when started in measure mode (`shell/assembly.sh start --counters`, which
  exists today). They can use frame callbacks, so they can measure everything they measure today: frame gaps,
  key-to-paint latency, and the counters. They never ship in a normal run. The soak becomes a script run from
  outside the app, which drives the page through the socket and the native UI tool.
- **B. A backend "clock" topic.** The backend's one shared timer pushes a tick, and the page samples its counters on
  each tick. This can sample anything the page counts: open sockets, listeners, subscriptions, rows drawn, and memory
  (the Mac backend reads it). It **can't** measure frame gaps or key-to-paint latency, because those need the page's
  own frame callback. With only B, the lag meter loses its two main numbers.
- **A and B together.** Counters come from the clock topic, which always works, including in a normal run.
  Frame-timing measurement stays a measure-mode tool, as in A.

Whichever Jesse picks, the soak's waits (section 7, row 4) leave the page.

**`app/measure/` is the one place the timer rule doesn't reach.** That makes it a place a model could put a timer,
or grow a second app, so under A it gets these safeguards:

- The guard scans `app/measure/` too, and allows only timers and frame callbacks there. `fetch`, Tauri imports,
  `WebSocket` outside `socket.ts`, and `.then`/`.catch`/`.finally` fail there as they do everywhere, so it can't
  become a second app.
- `app/measure/**` goes in `scripts/locked-paths.txt`, so adding or changing a file there needs an OK.
- The guard fails if anything in `app/src` imports from `app/measure`.
- The normal build must not include it. Only `shell/assembly.sh start --counters` injects it, and a check on the
  normal build's output fails if a measure file is in it.

**Decided by Jesse, 2026-10-26 19:27Z (card): A and B together,** with those safeguards. The counters come from
the clock topic and work in every run. Frame timing, which only the page can measure, stays a locked measure-mode
tool.

### D3. Clean Architecture: the article or the EdiPlatform layout

The two don't match, and I need to know which to follow.

- **The article** (codewithmukesh.com/blog/clean-architecture-dotnet) uses four projects: `Domain` (no packages),
  `Application` (interfaces, DTOs, services, validators, and `IApplicationDbContext`), `Infrastructure` (the
  DbContext and outside systems) and `Api` (endpoints and `Program.cs`). Dependencies point inward:
  `Api → Infrastructure → Application → Domain`. There's no repository, no MediatR and no AutoMapper, and each layer
  has a `DependencyInjection.cs` with `AddApplication()` / `AddInfrastructure()`.
- **EdiPlatform and RentalCommand** (read on agent-workbox) use `Core`, `Data`, `Api` and `Engine`. There's no
  Application project. App services live in `Api/Services/` and use the concrete DbContext directly.
  RentalCommand's write kernel is the closest thing to the "shared atomic save" Jesse describes:
  - `RentalCommand.Core/Atomic/AtomicCommandContracts.cs` defines `IWriteExecutor`.
  - `RentalCommand.Data/Atomic/WriteExecutor.cs` runs every write in one transaction.
  - `RentalCommand.Data/Auditing/AtomicAuditSaveChangesInterceptor.cs` writes the audit.
  - `RentalCommand.Data/DependencyInjection/AtomicPersistenceKernelExtensions.cs` registers it all.
- The `image.png` his comment refers to isn't in commit `9e51121`, and I couldn't find it on agent-workbox.

**Decided by Jesse, 2026-10-26 19:27Z (card): the article's layout plus RentalCommand's write executor.** He added,
the same minute: "You don't need EF Core, i'd rather plain SQL instead anyways, I can read it better than ef core... I was just pointing to the structure he had, with no repository pattern. Which every other Clean Architecture has. If you can just have the core of the app like that, then break up the rest by feature, that should be organized enough, then, that there aren't 50 files per directory, and other lanes have a plain straight way to follow."

So the core of the app is in the article's layers (Domain, Application, Infrastructure, and the two hosts), with
RentalCommand's idea of one write executor that every write goes through (`IAtomicWriter`). Everything else is one
folder per feature, with no repository and plain SQL. Section 4 has the layout.

### D4. Bytes over the socket

One path means attachments go over the socket too. Today, `POST /threads/{id}/attachments` and
`GET .../attachments/{name}` are the only binary HTTP calls. A 10 MB image, the size the composer allows today, is
about 13.3 MB as base64. That doesn't fit the 4 MB frame limit in 5.1, so the image has to be split up, or the limit
changed. The options:

- **A. Chunked calls.** `attachments/begin` returns an upload id, then `attachments/chunk` sends about 1 MB of image
  per call (about 1.33 MB as base64, well under 4 MB), appended to a staging file. `attachments/commit` checks the
  size and type, and links the file in one atomic write. A 10 MB image is about 10 short calls.
  - Each call stays well under the 30 s limit, so this fits the "calls are short" rule in 5.1.
  - Progress comes for free, and cancelling is a `cancel` on the next chunk.
  - Other frames, such as rows and saves, still get through between chunks.
  - Reading an image back is the same thing in reverse: `attachments/read` with an offset, about 1 MB a call.
- **B. Binary frames.** A new frame type: a small JSON header, then the raw bytes. There's no base64 cost, but it's a
  second frame format for both sides to read and test, and a 10 MB frame still needs either chunking or a higher
  limit.
- **C. A higher frame limit,** 16 MB. This is the simplest to write. But every big image ties up the socket while it
  goes, so rows and saves wait behind it, and the backend's memory per frame quadruples.

**Decided by Jesse, 2026-10-26 19:27Z (card): A, chunked calls** of about 1 MB, with one atomic commit. It keeps one frame format and one path, keeps
the 4 MB limit, and doesn't block other traffic.

## 2. Where Jesse's comments went

| His comment (in `docs/# Assembly .md` or his 26 October message) | Where it is in this plan |
| --- | --- |
| No old data to bring over | Section 3, rule 14: no migration of old data, the schema starts fresh. |
| Find out why MCP servers weren't a problem in the old app before changing them | Section 11, phase C3, starts with that investigation, and changes nothing until it has an answer. |
| One language server shared by every session, not one per session | Section 11, phase W5: one server per language per checkout, shared, and stopped when the last editor closes. |
| Machine-to-machine messaging works in the old app; the timers came out of it | Section 11, phase P1 reads the old messaging code as the reference. Its polling (`RemoteOutbox.cs:44`, every second) becomes a push on the machine socket. |
| A base implementation on the backend like the front end's, as a PR for review before merging | Section 10: the base is built as commits on one branch, with the preferences feature rebuilt as the example every feature copies. It goes out as five PRs, one per step group, each with a plain summary for Jesse, and he merges each himself. |
| Shared plumbing for repeated work (atomic save, logging, auditing, socket send and receive) through middleware or DI, with features calling an interface | Sections 5.2 to 5.8: one implementation each, registered in DI, wrapped around every call by the dispatcher. |
| Clean Architecture after codewithmukesh, DbContext rather than repositories | Sections 4 and 5.6, and decisions D1 and D3. |
| Timers and ticks made the old app unusable; one allowlist entry grew to more than 50 | Sections 6 to 9: no timers in `app/src`, and no allowlist file. The guard has no list anyone can add to. |
| No allowlist at all; the debounce is the only timer he'd allow, unless there's another way | Section 5.9: the other way. A save costs about 17 µs, so the page saves each change as it happens (every keystroke, each toggle, the end of a drag), one atomic write each. There's no debounce in the front end or the backend, and no exception. |
| Any timer comes from the backend, even one shared timer that pushes timings to the front | Section 5.8: one backend clock, a `PeriodicTimer` on `TimeProvider`. It pushes on the `clock` topic only while someone is subscribed. |
| HTTP and WebSocket both is confusing; one path | Section 5.1: one WebSocket for everything the page does. The only other HTTP is `/mcp`, which agents use. |
| Saves are atomic: one save per save call, or one transaction | Section 3, rule 6, and section 5.6. |
| `async`/`await`, not promise chains | Section 3, rule 4; section 8 lists every chain today; the guard rejects `.then(`, `.catch(` and `.finally(`. |
| Bring mac-command-bar's AGENTS.md over | Section 10, step B1, with the merged contents in section 3. |
| Sol's doc isn't the detailed implementation plan | This document. Sol's stays as the feature catalogue. Section 11 maps its steps P01 to P36 onto these phases. |

## 3. The rules (what AGENTS.md will say)

Step B1 merges mac-command-bar's `AGENTS.md` (at `d5f5fe68c`) into this repo's. These are the rules that make one
way the only way. Each one names what enforces it.

1. **No timers, ticks, frame waits or microtask tricks in `app/src`.** That covers `setTimeout`, `setInterval`,
   `requestAnimationFrame`, `queueMicrotask`, Svelte's `tick()`, `AbortSignal.timeout`, and anything that waits on
   time. There are no exceptions and no allowlist. What needs time comes from the backend, as a push (section 5.8),
   from CSS (a finite animation), or from an event (`ResizeObserver`, a shell event). Enforced by
   `app/scripts/guards.sh`.
2. **One transport.** The page talks to the backend only through `app/src/lib/socket.ts`, over one WebSocket.
   Nothing else in `app/src` calls `fetch` or opens a `WebSocket`. Enforced by the guard, which names that one file
   inside the script.
3. **Tauri, and every window, document and media-query listener, only in `app/src/lib/host/`.** A listener in any
   form (`addEventListener(`, `<svelte:window`, `<svelte:document`, `matchMedia(`, Svelte's `MediaQuery`) lives in
   host, and features subscribe to what host exposes. Events on an element use Svelte's `on…` attributes. Enforced by
   the guard, which names that folder inside the script.
4. **`async`/`await` with an `AbortSignal`, never `.then`, `.catch` or `.finally`.** Enforced by the guard.
5. **Every backend call has a cancellation token, and passes it to everything it awaits.** CA2016 is a build error
   (`dotnet/.editorconfig`). Closing the socket cancels every call still running on it.
6. **Every save is atomic.** Every write runs inside `IAtomicWriter.Write`: one connection, one transaction, an
   audit row, commit, and only then publish. That includes a call's write and the chat row writes (`RowStore`'s
   batches). Nothing can write any other way, because only the writer hands out a write connection. Enforced by the
   type: `IAppDatabase` is read-only.
7. **Features register only through the dispatcher.** A feature adds its calls and topics in its
   `<Feature>Feature.cs`. No feature maps a route, reads a socket or opens a database connection itself. Enforced by
   `scripts/lint.d/40-one-dispatcher.sh`, which replaces `40-routes.sh`.
8. **Core in layers, everything else one folder per feature.** Domain references nothing, Application references
   only Domain, Infrastructure references Application, and the hosts reference both. A feature lives in
   `Application/Features/<Feature>/` and never uses another feature's folder. No repository, plain SQL, and no folder
   over 30 files. Enforced by the project references and by `ArchitectureTests` (step B2).
9. **One class per file, organized by feature inside each layer.** This is today's rule, kept.
10. **Animations end.** Short motion tied to an interaction is fine (120–250 ms); a loop at rest isn't. From
    mac-command-bar. A loop runs only under `[aria-busy="true"]`, which the element sets only while real work runs.
    Enforced by the guard's loop rule (9.1) and the at-rest check in B10.
11. **No CSS `:has()`.** Enforced by the guard.
12. **TypeScript only.** No `.js`, `.mjs` or `.cjs` files. This is today's rule; `scripts/lint.d/00-typescript-only.sh`
    enforces it.
13. **Filtering, sorting, counting and paging happen in SQL.** From mac-command-bar.
14. **No old data comes over.** The schema starts fresh, and there's no migration from the old app. From Jesse's
    comment.
15. **KISS, YAGNI, and one way only.** A change is done when the old way is deleted. From mac-command-bar, kept word
    for word.
16. **mac-command-bar's sections that carry over, adapted to this repo.** Each was checked for references to the
    Rust app or to tools this repo doesn't have:
    - **"What we don't trade away":** kept. "Tauri command or websocket message" becomes "socket frame".
      `scanSchedulers.ts` and the allow-list sentence become `app/scripts/guards.sh`, with no list. Tailwind `has-*`
      joins the `:has()` rule, now that Tailwind is in (91a01e6).
    - **"Things that do real damage":** kept. The data folder paths become this repo's two (from "Safety"), and
      `fourScenarios.ts start --seed` and `docs/operations/four-scenario-check.md` become `scripts/check4.sh` and its
      README.
    - **"Providers behave the same":** kept. The provider folder becomes `Assembly.Infrastructure/Acp/Providers/`.
      Two lines go, because neither exists here: the pre-commit count of provider-name branches, and
      `provider-updates.yml`.
    - **"Check every place the change reaches":** kept. `remote/wire.rs` becomes the socket protocol
      (`SocketFrames.cs`), and the `phone/` line waits for phase P4.
    - **"Testing":** kept as rules: test first, no sleeps, no tests of layout or styling, check4 for every change.
      Dropped: the commit-msg `fix` hook, the `pnpm` and `cargo` commands, `assembly-native-ui`, and
      `evidence add --task TSK`. This repo's own "Checking a feature" section says how to prove a change.
    - **"Review guidelines":** kept unchanged.
    - **"Reaching Jesse":** rewritten. The Build Board and `board_update` don't exist here yet (the Build Board is
      rebuilt in phase P3), so a question or a
      blocker goes to the project thread, with enough context to answer without reading the chat. The task waits,
      and other work goes on.
17. **Dropped from mac-command-bar,** because they describe the Rust app or tools this repo doesn't have:
    - the Rust folder map;
    - the 600-line baseline file (the size rule waits for Jesse's 300-line decision);
    - the `Architecture:` commit line and `main-architecture-explained.html`, which come back when that page is
      written for this repo;
    - the Notion task rules and `tsk-<id>` branch names;
    - "The build board" section: the old Rust board's reporting rules;
    - "The workbox" section's mac-command-bar worktree path. This repo's "Lanes" section already says where lanes
      live.

## 4. The solution layout

Jesse's decision (D3): the core of the app follows the article's layers, everything else is split by feature with
one folder each, there's no repository, and the SQL is plain.

**Where a new feature's files go.** Its backend code goes in `dotnet/Assembly.Application/Features/<Feature>/`, its
tests in `dotnet/Assembly.Tests/Features/<Feature>/`, and its page code in `app/src/lib/<feature>/`. When it needs an
outside system (git, a terminal, files on disk), the contract stays in its feature folder and the implementation goes
in `dotnet/Assembly.Infrastructure/<Feature>/`, with the same name. Only plumbing that several features share goes in
the layers. No folder holds more than 30 files (section 12), so a feature that grows gets subfolders, and no folder
gets anywhere near 50. The limit counts files, not subfolders: `lib/components/ui/` has one subfolder per shadcn
component, and each of those is counted on its own.

```text
dotnet/
  Assembly.slnx
  Directory.Build.props          net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors (today's)
  Directory.Packages.props       new: one version per package
  .editorconfig                  today's, CA2016 an error
  Assembly.Domain/               references nothing
    Events/  Rows/  Threads/  Markdown/
  Assembly.Application/          references Domain only: no Microsoft.Data.Sqlite, no ASP.NET Core
    Common/
      Database/                  IAppDatabase, IWriteScope, IAtomicWriter, IRow, SqlArg
      Calls/                     ICallRegistry, IFeature, CallContext, CallFailed
      Topics/                    ITopics
      Clock/                     IBackendClock
      Machines/                  IMachine, MachineSet
      Agents/                    IAgentLauncher, IAgentLink, AgentDescriptor, SharedAdapter
      Acp/                       the ACP protocol over streams: messages, V1, JSON-RPC, AcpClient
    Features/
      Chat/                      ChatFeature.cs, ChatSchema.cs, and Threads/ Rows/ Sessions/ Helpers/ Attachments/
      Preferences/               step B7
      Diagnostics/               step B8
    DependencyInjection.cs       AddApplication(), AddMachineFeatures(), AddMacFeatures()
  Assembly.Infrastructure/       references Application
    Persistence/                 AppDatabase, AtomicWriter and the audit, the schema runner
    Socket/                      SocketEndpoint, SocketConnection, SocketDispatcher
    Topics/  Clock/  Agents/  Machines/  Remote/  Transcript/  Mcp/
    Http/                        today's HTTP routes, FeatureCalls and the feature lists, until B7, B8 and C1
                                 delete them
    DependencyInjection.cs       AddInfrastructure(), AddMacInfrastructure()
  Assembly.Mac/                  Program.cs, and MacOptions.cs for its arguments
  Assembly.Remote/               Program.cs, and its Serve, Smoke, Record and Replay commands
  Assembly.Tests/                Architecture/ Core/ Infrastructure/ Features/<Feature>/, plus today's Draft/,
                                 Pinned/ and Fixtures/
```

The tests stay in one project rather than one per layer. This keeps the locked paths
(`dotnet/Assembly.Tests/Pinned/**` and the READMEs in `scripts/locked-paths.txt`) where they are, so the move needs
no OK for them.

### What goes where

| Place | Holds | Doesn't hold |
| --- | --- | --- |
| Domain | Only the records and rules that several features share: agent events, row kinds and payloads, `ThreadInfo` and its config records, and the markdown block records (`Safe*`), which the row payloads hold. Time comes in as an argument. | A record only one feature uses (that stays in the feature), I/O, JSON, SQL, packages. |
| Application `Common/` | The shared contracts every feature uses: the database and writer, calls, topics, the clock, machines and agent launching. Also the ACP protocol code, which only speaks JSON-RPC over the streams it's given: it starts no process and touches no files, and the chat sessions are built on its records. | Implementations that start processes, open SQLite or serve HTTP. |
| Application `Features/<Feature>/` | Everything one feature is: its `<Feature>Feature.cs` (calls and topics), its records, its rules, its tables (`<Feature>Schema.cs`), and its plain SQL, written straight against `IAppDatabase` for reads and `IWriteScope` for writes. | A repository, a class per table, or another feature's types. |
| Infrastructure | The implementations of `Common/` and the outside systems: the SQLite connection and the writer with its audit, the socket and dispatcher, topics, the clock, starting agent processes and git, local and remote machines (SSH tunnel, follower, outbox), the provider transcript readers, `/mcp`, and today's HTTP routes until they're deleted. | Feature rules or feature SQL. |
| Hosts | `Program.cs`: parse arguments, call the `Add*` methods, map the socket endpoint and `/mcp`, run. | Anything else. |

**What Application never uses.** Application and Domain never reference `Microsoft.Data.Sqlite`, ASP.NET Core or
`System.Diagnostics.Process`; `ArchitectureTests` checks all three. Any other package is fine if it's a pure library
with no I/O, such as Markdig and DiffPlex, which chat's rows use to parse markdown and diffs.

**Features don't reach into each other.** A feature folder never uses a type from another feature's folder. When
two features need the same thing, it moves to the core (a record to Domain, a contract to `Common/`).
`ArchitectureTests` checks it (step B2). Infrastructure may use Application's types, since it references
Application: the machine links write chat's rows, for example.

**Why chat is one feature.** Threads, rows, sessions, helpers and attachments call each other all the time today
(the helper tools use `ThreadStore` and `SessionRegistry`; a send carries attachments). Split into five features,
each would reach into the others. As one `Chat` feature with five subfolders, each under 30 files, they don't.

**No repository under another name.** `RowStore`, `ThreadStore` and the preference reader and writer don't become
classes that wrap a table. Each feature reads with its own SQL through `IAppDatabase` and writes through
`IAtomicWriter`, in its own folder. The SQL is named for what it does (`CommitBatch`, `PageAfter`), and only its own
feature uses it.

### The page mirrors it

`app/src/lib/` is already mostly one folder per feature. Under this layout:

- A feature's page code is `app/src/lib/<feature>/`, with the same name as its backend feature. It imports only the
  core and its own files, never another feature's folder (`ArchitectureTests` checks this too).
- The page's core is `lib/socket.ts`, `lib/host/`, `lib/components/` (from the registry), `lib/time/` and
  `lib/utils.ts`.
- Chat's page folders today (`chat/`, `composer/`, `rows/`, `thread/`, and `Conversation.svelte`) move under
  `lib/chat/` in step C1, when chat moves onto the socket. `call.ts` goes in B7, `api.ts` in C1, and `debug.svelte.ts`,
  `lag.svelte.ts` and `soak.ts` move to `app/measure/` in B9 (D2).

### Where today's folders go

Today's folders are listed in `dotnet/README.md`. Step B2 moves them as below, renaming only namespaces. Behavior
doesn't change in that step.

| Today | Goes to |
| --- | --- |
| `Assembly.Engine/Events/` | `Assembly.Domain/Events/` |
| `Assembly.Engine/Rows/Payloads/`, `RowKind` | `Assembly.Domain/Rows/` |
| `Assembly.Engine/Threads/ThreadInfo`, `ThreadConfig`, `HelperMessage` | `Assembly.Domain/Threads/` |
| `Assembly.Engine/Markdown/Safe*` block records | `Assembly.Domain/Markdown/` |
| `Assembly.Engine/Markdown/SafeMarkdown`, `SafeLinks`, `BlockReader`, `InlineReader`, `MarkdownJsonContext` (the parse, with Markdig) | `Assembly.Application/Features/Chat/Rows/Markdown/` |
| `Assembly.Engine/Threads/ThreadStore`, `ThreadPage` | `Assembly.Application/Features/Chat/Threads/` |
| `Assembly.Engine/Rows/` (the rest: `RowConverter`, `RowBatch`, `OpenTurn`, `RunRecovery` and the like) | `Assembly.Application/Features/Chat/Rows/` |
| `Assembly.Engine/Store/` (`RowStore`, `RowPage`, `RowWire`, `StoredRow`, `StoredToolCall`) | `Assembly.Application/Features/Chat/Rows/` |
| `Assembly.Engine/Store/Schema` | `Assembly.Application/Features/Chat/ChatSchema.cs` |
| `Assembly.Engine/Sessions/` (all but `SharedAdapter`) | `Assembly.Application/Features/Chat/Sessions/` |
| `Assembly.Engine/Sessions/SharedAdapter` (it only uses `IAgentLauncher`) | `Assembly.Application/Common/Agents/` |
| `Assembly.Engine/Helpers/` tools | `Assembly.Application/Features/Chat/Helpers/` |
| `Assembly.Engine/Attachments/ImageType` | `Assembly.Application/Features/Chat/Attachments/` |
| `Assembly.Engine/Attachments/AttachmentStore` (it writes files) | `Assembly.Infrastructure/Chat/Attachments/` |
| `Assembly.Engine/Acp/` (messages, `V1`, `JsonRpc`, `AcpClient`, `IPermissionPolicy`) | `Assembly.Application/Common/Acp/` |
| `Assembly.Engine/Agents/IAgentLauncher`, `IAgentLink`, `AgentDescriptor`, `AgentCommand` (a record `AgentDescriptor` makes) | `Assembly.Application/Common/Agents/` |
| `Assembly.Engine/Agents/` (the rest: `AdapterLauncher`, `AgentProcess`, `ProcessGroup`, `GitCheckout`, `AgentConnection`) | `Assembly.Infrastructure/Agents/` |
| `Assembly.Engine/Live/` (`LiveHub`; it uses chat's rows) | `Assembly.Application/Features/Chat/Rows/` until C1 turns it into the `rows` topic |
| `Assembly.Engine/Machines/IMachine`, `MachineSet`, `MachineUnreachable` | `Assembly.Application/Common/Machines/` |
| `Assembly.Engine/Machines/LocalMachine` | `Assembly.Infrastructure/Machines/` |
| `Assembly.Engine/Transcript/TranscriptLog` (the ACP connection writes it, to a writer it's given) | `Assembly.Application/Common/Acp/` |
| `Assembly.Engine/Transcript/TranscriptReplay` | `Assembly.Infrastructure/Transcript/` |
| `Assembly.Engine/Database/` (the plain-SQL contracts, B2a) | `Assembly.Application/Common/Database/` |
| `Assembly.Engine/Persistence/` (`AppDatabase`, `AtomicWriter`, B2a) | `Assembly.Infrastructure/Persistence/` |
| `Assembly.Engine/Api/McpRoutes` | `Assembly.Infrastructure/Mcp/` |
| `Assembly.Engine/Api/` (the rest: HTTP routes, `LiveSocket`, `TokenGuard`) | `Assembly.Infrastructure/Http/`, kept only until each route moves onto the socket, then deleted (section 11) |
| `Assembly.Engine/Features/` (`FeatureCalls`, `FeatureContext`, `CallFailed`, `MachineFeatureList`) and `Assembly.Mac/Features/MacFeatureList` | `Assembly.Infrastructure/Http/`; deleted in step B7, replaced by `Socket/` and `Application/DependencyInjection.cs` |
| `Assembly.Mac/Remote/` | `Assembly.Infrastructure/Remote/`, registered only by `AddMacInfrastructure()` |
| `Assembly.Mac/Preferences/` (`PreferenceStore`, `PreferencesSchema`, `README.md`) | `Assembly.Application/Features/Preferences/` |
| `Assembly.Mac/Preferences/PreferencesRoutes` | `Assembly.Infrastructure/Http/` until B7 deletes it |
| `Assembly.Mac/Page/TurnActivity` | `Assembly.Application/Features/Diagnostics/` (step B8 finishes it) |
| `Assembly.Mac/Page/ImageFile`, `Thumbnail`, `MemorySample` (they read files, call ImageIO, and run `ps`) | `Assembly.Infrastructure/Diagnostics/` |
| `Assembly.Mac/Page/PageRoutes`, `Assembly.Mac/Debug/DebugRoutes` | `Assembly.Infrastructure/Http/` until B8 deletes them |
| `Assembly.Mac/Serve/` | Stays until B7, which makes it `Assembly.Mac/Program.cs` and `MacOptions.cs` |
| `Assembly.Remote/Serve`, `Smoke`, `Record`, `Replay` | Stay in `Assembly.Remote/` (they're that host's commands) |

## 5. The shared plumbing

Each part has one implementation, registered in DI, and features reach it only through its interface.

### 5.1 One socket, its frames

- **Endpoint.** The page opens `ws://127.0.0.1:<port>/socket` and connects nowhere else. The Mac backend opens the
  same endpoint on each remote host, through the SSH tunnel (phase C2). Both machines speak the same protocol.
- **Frame format.** Every frame is one JSON text message with a `t` field naming its type. The JSON uses
  source-generated serialization (`SocketJson.cs`), so it works under AOT.

| Direction | `t` | Fields | Meaning |
| --- | --- | --- | --- |
| page → backend | `hello` | `token`, `protocol` | The first frame. A wrong token or protocol closes the socket with code 4401 or 4400. The token is never put in the URL. |
| backend → page | `welcome` | `protocol`, `machine` | The socket is ready. |
| page → backend | `call` | `id`, `feature`, `method`, `args` | Run one call. `id` is unique on this socket. |
| page → backend | `cancel` | `id` | Stop that call. Its handler's token fires. |
| backend → page | `result` | `id`, `result` | The call finished. A write's result means it committed. |
| backend → page | `error` | `id`, `code`, `message` | `not_found`, `bad_args`, `failed`, `cancelled`, `timeout`, `unreachable`. |
| page → backend | `sub` | `id`, `topic`, `key?`, `after?` | Subscribe. The backend sends the current state, then each change. `after` is a sequence number, for the rows topic. |
| page → backend | `unsub` | `id` | Stop that subscription. |
| backend → page | `state` | `sub`, `items` | The full state, once, right after `sub`. |
| backend → page | `change` | `sub`, `op` (`upsert`/`remove`), `item` or `id` | One change to one item. |
| backend → page | `rows` | `sub`, `rows` | A batch of rows for the rows topic, in order, with `prevSeq` as today. |

The limits are fixed, not settings:

- 4 MB per frame, and 16 MB queued per connection. A slow page is closed and can reconnect from its last sequence.
- 30 seconds per call (`CancelAfter` in the dispatcher). A call that runs over answers `timeout`. There's no
  exception to the 30 s limit, ever, saves included.

**Calls are short.** Long work, such as a clone, a big search or a git operation, is *started* by a call, which
answers at once with a job id. The work then reports its progress and its result on a topic keyed by that id, and
has its own cancel call. No handler is allowed to wait on long work inside a call.

### 5.2 The dispatcher: one path for every call

`Application/Common/Calls/ICallRegistry.cs`:

```csharp
public interface ICallRegistry
{
    void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<TResult>> handler);
    // The async part runs first, with the call's token, for anything outside the database (upload bytes, files,
    // hashing, another machine). The function it returns is the database part, run once inside IAtomicWriter.Write.
    void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler);
    // A setting has no outside work, so a save is only the database part.
    void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> write);
    void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<string?, CallContext, Task<IReadOnlyList<TItem>>> state);
}
```

`CallContext` carries the call's `CancellationToken`, the connection id, the machine, and a logger scope.

`Infrastructure/Socket/SocketDispatcher.cs` runs the same steps around every call, so no feature can skip one:

1. Find the handler. If there isn't one, answer `not_found`.
2. Read `args` with the handler's `JsonTypeInfo`. If that fails, answer `bad_args`.
3. Start a logger scope (`feature/method`, the call id, the connection) and log the call at Debug.
4. Make the token, linked from the connection's token, the call's `cancel` frame, and the 30-second limit.
5. For `Write` and `Save`, put the call on the connection's write queue, which runs one at a time in arrival order:
   a `Write`'s async part, then its database part inside `IAtomicWriter.Write`; a `Save` inside `IAtomicWriter.Write`
   (5.6, 5.9). So each call makes exactly one write. For `Read`, call it directly, concurrently.
6. Answer `result`. A `CallFailed` answers `failed` with its message. A cancellation answers `cancelled`, or
   `timeout`. Anything else is logged at Error and answers `failed` with "feature/method failed". This keeps
   today's 500 handling (`FeatureCalls.cs`).

A feature's file is short, and its SQL is in it. For example,
`Application/Features/Preferences/PreferencesFeature.cs`:

```csharp
internal sealed class PreferencesFeature(IAppDatabase db) : IFeature
{
    const string All = "SELECT key, value FROM preferences";
    const string Upsert = "INSERT INTO preferences (key, value) VALUES ($key, $value) "
        + "ON CONFLICT (key) DO UPDATE SET value = excluded.value";

    public void Register(ICallRegistry calls)
    {
        calls.Tables(PreferencesSchema.Create);
        calls.Topic("preferences", PreferencesJson.Default.ListPreference,
            (_, call) => Task.FromResult<IReadOnlyList<Preference>>(db.Query(All, Preference.Read, call.Cancellation)));
        calls.Save("preferences", PreferencesJson.Default.JsonElement, (key, value, scope) =>
        {
            scope.Execute(Upsert, new("$key", key), new("$value", value.GetRawText()));
            scope.Publish("preferences", key, new Preference(key, value), PreferencesJson.Default.Preference);
        });
    }
}
```

`calls.Tables` hands the feature's `CREATE TABLE IF NOT EXISTS` statements to the schema runner, which runs every
feature's at startup, through the writer, before the socket opens.

`AddMacFeatures()` registers `PreferencesFeature`. `AddMachineFeatures()` registers what every machine serves.

### 5.3 Cancellation

- The page's `call(feature, method, args, { key, signal })` sends `cancel` when its signal aborts. A newer call with
  the same `key` aborts the older one; this is base#10, kept.
- The backend's token reaches the handler, and from there everything it awaits (rule 5).
- Closing the socket cancels every call still running on it.
- A forwarded call to a remote machine sends `cancel` on the machine socket (phase C2).

### 5.4 Errors on the page

`socket.ts` returns `Outcome<T>`, as `call.ts` does today: `{ ok: true, result }` or
`{ ok: false, why: 'failed' | 'aborted' }`. A failure is reported once through `onFailure`, which is set in
`routes/+page.svelte`.

### 5.5 Logging

There's one logger scope per call (5.2, step 3), and every connection logs its open and close at Information. Tokens
never reach a log. Features don't add their own call logging.

### 5.6 Atomic writes and the audit

`Application/Common/Database/IAtomicWriter.cs`:

```csharp
public interface IAtomicWriter
{
    // Synchronous on purpose: the body can't await, so no transaction can be held open across an agent call, a
    // network call or a wait. The compiler enforces it.
    T Write<T>(string what, Func<IWriteScope, T> write);
}

// Reads. Plain SQL, mapped by a function the feature writes; no reflection, so it's safe under Native AOT.
// The token cancels the read: it's wired to SqliteCommand.Cancel (sqlite3_interrupt) through token.Register.
public interface IAppDatabase
{
    List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args);
    T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args);
}

// Inside one write: its reads run on the write connection, so they see the write's own transaction.
public interface IWriteScope : IAppDatabase
{
    int Execute(string sql, params SqlArg[] args);
    void Publish<T>(string topic, string? key, T change, JsonTypeInfo<T> type); // queued, sent after commit
}

// One result row, read by column position. So every query names its columns: never SELECT *.
public interface IRow
{
    bool IsNull(int column);
    long Long(int column);
    double Real(int column);
    string Text(int column);
    byte[] Blob(int column);
}

public readonly record struct SqlArg(string Name, object? Value); // a string, long, double, byte[] or null
```

These live in `Application/Common/Database/`. Since rows are read by position, a query always names its columns
and never uses `SELECT *`; `scripts/lint.d/45-no-select-star.sh` (step B3) fails on `SELECT *` anywhere under
`dotnet/`. Application never references `Microsoft.Data.Sqlite`: the
Infrastructure classes turn a `SqlArg` into a `SqliteParameter` and wrap the data reader as an `IRow`. There's no
Dapper and no other new package.

**Connections.** One `AppDatabase` per database file owns the one `AtomicWriter`, and every store and feature on
that file uses that one. The writer keeps one long-lived write connection behind one lock, held for the whole write.
A plain lock is right, because the body can't await. Reads outside a write never use the write connection: each read
takes a pooled read connection of its own, so a slow read never holds up another. That was measured (step B2a): with
one shared read connection, a thread list would wait behind a 200 ms page read, where today it takes about 90 µs.

`Infrastructure/Persistence/AtomicWriter.cs` handles every write the same way:

1. Take the database's one write lock.
2. Begin a transaction on the one write connection.
3. Run the feature's write.
4. Insert one audit row: `audit(at, what)`, where a call's `what` is `feature/method`. B4 adds the connection id as one more column.
5. Commit, then send the queued publishes.
6. If the write fails, roll back and publish nothing.

No transaction is ever held open across an agent call, a network call, or a wait for a socket. No file, network or
process work happens inside `Write` either: it's done first, in the handler's async part, so a 10 MB attachment never
holds the write lock.

The writer handles every write, not just calls. Chat's row writes (today's `RowStore` `Commit` and `Copy`) run
through `IAtomicWriter` too, and so do its thread writes (today's `ThreadStore`), since only the writer hands out a
write connection. Each row
batch gets one audit row, not one per row. Every write is audited, rows included, with no carve-out.

**Audit retention: 30 days, pruned by the writer, with no timer.** A busy month of chat adds millions of audit rows,
so the writer keeps only the last 30 days. There are two prunes:

- **At startup.** When the backend starts, the writer deletes audit rows older than 30 days, once.
- **While it runs, after a write.** A remote host can run for weeks without a restart. So on every write, the writer
  reads the time from `TimeProvider`, and if its last prune was more than a day ago, it runs a prune. The prune is
  never inside the write that triggered it: that write commits and publishes first, so a keystroke save or a chat
  rows batch never carries the delete. The prune then runs as its own transaction on the writer, in the same queue
  as every other write.

This was chosen over a background timer, because it adds no timer and needs no thread of its own. A host that writes
nothing has nothing to prune.

Each prune deletes at most 5,000 expired rows, using an index on `audit(at)`, so it stays short and the write queued
behind it waits little. If a prune hits the cap and expired rows remain, the writer doesn't mark the day's prune done,
so the next write triggers another one. A backlog (a host that was off for months) clears over the next few writes
instead of waiting a day per 5,000 rows.

**Measured** in the same scratch program as 5.9: a 20-row batch with 400-byte payloads takes 131 to 138 µs at the
median with its audit row, against 96 to 99 µs without. The 99th percentile is 2.7 to 3.4 ms with the audit row and
2.6 to 2.7 ms without; most of that is the WAL write. Step B3 measures it again through the real writer.

Reads go through `IAppDatabase`, which has no write methods.

### 5.7 Topics

`Infrastructure/Topics/Topics.cs` keeps each connection's subscriptions. When a page subscribes, it gets
`state(key)`, then each change the writer published for that topic and key. Unsubscribing, or closing the socket,
removes the subscription. The `rows` topic is today's `LiveHub` (phase C1).

### 5.8 The one backend clock

`Infrastructure/Clock/BackendClock.cs` is one `PeriodicTimer` on the injected `TimeProvider`.

- **Each subscription picks its granularity** with its key:
  - `second`, for a running turn's elapsed time;
  - `minute`, for relative times like "5m ago";
  - and, under D2 B, `counters`, every 2 s.
- **It sends only what's asked for.** A `minute` subscriber gets one `change` a minute, on the minute. The timer
  ticks at the finest granularity any current subscription asks for, and stops when there are none. The `change`
  carries `{ now }`.
- **Only one component reads `now`:** `app/src/lib/time/Time.svelte`, which shows either an elapsed time or a
  relative time. Every other place that shows a time uses `<Time>`, so the clock has one reader on the page.
- **Tests** use `FakeTimeProvider`, as the spike does now.

The backend's other waits are listed in the timer inventory: the `SessionHost` coalesce timer, the adapter idle
grace, the SSH backoff, and the follower's backoff. They stay where they are, since they're backend work that needs
time and isn't a page clock. Phase P1 replaces two polls with pushes: `RemoteOutbox.cs:44`, every second, and
`MessageStatusTool.cs:30`, every 250 ms.

### 5.9 Saves: each change as it happens, with no debounce anywhere

This answers Jesse's "find another way to do what the debounce does". There's no debounce in the front end or the
backend. A debounce exists to turn many changes into one save, to keep saves cheap. A save here is cheap enough
(measured below) to send every change, so nothing ever sits unsaved in the page.

**When the page saves:**

- A settings text field saves on every `input`, one call each. That's about 10 a second at typing speed.
- A toggle or a select saves on `change`.
- A divider saves on `pointerup` at the end of the drag. Quitting mid-drag isn't a real case.

**Each save is one call and one atomic write.** `save(feature, key, value)` sends `call` with `feature/set`. The
handler runs once inside `IAtomicWriter.Write` (5.6): one upsert, its audit row, commit, then a publish on the
feature's topic, so other windows see it.

- There's no batcher, no timer, and no view that mixes in values still waiting to be written. A read returns what's
  committed.
- The page sets its own `$state` before it sends. The echo of a save back to the window that sent it is a no-op;
  an echo from another window is applied.

**What makes the last value win.** Frames arrive in order on one socket. The dispatcher then hands **one
connection's writes to the writer in the order they arrived**: each connection has one write queue, and its `Write`
and `Save` calls run one at a time from it. Reads still run concurrently. So two saves of the same key from one window
can't reach the writer out of order, and the last one sent is the last one committed. Two windows saving the same key
is decided by which commit lands last, and each window then shows that value from the topic. Sequence numbers per key
weren't chosen, because the per-connection queue gives the same guarantee with no extra field in every save.

**Quitting.** The shell doesn't hold the close or the quit, and there's no `closing` event, no `pagehide` and no
`visibilitychange`. The page sends each save in the same event handler that changed the value, so a change never
exists only in the page. At close there's nothing to flush except frames already sent, and the backend, which keeps
running after the window closes, handles those.

**Audit volume.** Each save writes one audit row, so typing writes one per keystroke. A typical settings edit (a
20-character value) is 20 audit rows. For settings saves the audit row records the time, the connection, the
feature, the method and the key, **not the value**: about 60 bytes, so a 20-keystroke edit adds about 1.2 KB. That's
fine for a settings table people change a few times a day, and it keeps values (which could be long text) out of the
audit. No batching or timer is added for it.

**Cost, measured.** A single-row settings upsert with its audit row, in one transaction, on agent-workbox, takes
17 µs at the median and 81 µs at the 99th percentile; without the audit row it's 12 µs and 55 µs. That's 2,000 runs
each, two runs, Native AOT, `Microsoft.Data.Sqlite`, on disk with WAL and `synchronous = NORMAL` (RowStore's
settings), from a scratch program. Ten saves a second while typing is under 1 ms of backend work a second. Step B6
measures it again through the real `IAtomicWriter` and records the number.

### 5.10 The page side

- **`app/src/lib/socket.ts`**, the only transport file. It has `call`, `save`, `subscribe` and `onFailure`. It opens
  the socket once, with port and token from `window.__ASSEMBLY__`. When the socket closes, it fails pending calls as
  `aborted`, tries to reconnect once, and if that fails waits for the shell's `backend-ready` event before trying
  again. Once reconnected, it subscribes again with each subscription's last key and `after`. There are no timers
  in any of this.
- **`app/src/lib/<feature>/<feature>.svelte.ts`**, one per feature. It subscribes once, keeps a `$state` map, and
  applies each `change` to one item.
- **`app/src/lib/host/backend.ts`**, the shell side. The shell (`shell/src/main.rs`) watches its backend child. When
  the backend starts or restarts, it sends a `backend-ready` event to the webview. Restarting is the user's action
  (a Reconnect button), never an automatic loop.
- **A visible "Disconnected" state.** If the socket drops while the backend is still up, no `backend-ready` event
  comes. So after the one reconnect fails, the page shows "Disconnected" with a Reconnect button. The button tries
  the socket again, and asks the shell to restart the backend only if the shell reports it has exited.

## 6. What stays HTTP

- **`/mcp`.** Agents call Assembly's tools over the MCP protocol, which runs over HTTP. The page never uses it.
- **Nothing else.** Every route the page or the Mac backend calls today moves onto the socket and is deleted
  (section 11).

## 7. Every timer in `app/src` today, and where it goes

These come from the inventory at `9e51121`, and each was checked by grep.

| # | Today (file:line) | What it's for | Where it goes |
| --- | --- | --- | --- |
| 1 | `lib/debug.svelte.ts:53,77,100`: `delay`, `frame`, `debounce` | The counted helpers | **Deleted**, once nothing uses them. |
| 2 | `lib/lag.svelte.ts:30,32`: frame loop. `:43`: `setTimeout(publish, 500)`. `:51-52`: two frame callbacks to time a key press. `:21`: `visibilitychange` | The lag meter: frame gaps and key-to-paint delay | **D2.** Under A, it becomes a measure-mode tool in `app/measure/lag.ts`, injected by the shell. Under B, the frame-gap and key timing are lost and only the counters remain. |
| 3 | `routes/+page.svelte:140` (`?counters=1`), `lib/soak.ts:493`: every 2 s | The counters sampler | **D2.** Under B, it samples on the backend `clock` topic. Under A, it goes with the measure tool. |
| 4 | `lib/soak.ts:226,238,251,287,291,308,321,350,355,359,361,368,373,388,401,404,424,426,452,468` | The soak's waits between its steps | **Deleted from the page.** The soak becomes `app/measure/soak.ts`, run from outside the app, which drives it through the socket and the native UI tool. Its waits live in that script. |
| 5 | `lib/rows/client.ts:52`: `delay(1000)` | Reopening `/live` after it drops | **A shell event.** `socket.ts` reconnects once, then on `backend-ready` (5.10). `rows/client.ts` is deleted in phase C1. |
| 6 | `lib/Conversation.svelte:265`: a frame loop (`rows/reveal.ts`) | Showing streamed text at a steady pace | **The backend.** `SessionHost` already releases text on its coalesce timer, so the page shows text as it arrives. The page's reveal loop and `rows/reveal.ts` are deleted. A fade on each new chunk, if wanted, is a finite CSS animation. |
| 7 | `lib/Conversation.svelte:459`: a frame callback, then `coverView` | After layout, checking the drawn rows cover the view, and fixing the virtualizer when they don't | **An event.** A `ResizeObserver` on the scroller's content runs after layout. `coverView` runs in its callback. |
| 8 | `lib/Conversation.svelte:245`: `delay(1000)` in a loop | The running turn's elapsed time | **The backend clock.** `<Time>` subscribes to `clock` with key `second` while a turn runs, and shows `now - startedAt` (5.8). |
| 9 | `lib/call.ts:52,84,99`: `AbortSignal.timeout(30 s)` | The call timeout | **The backend.** The dispatcher's 30-second limit answers `timeout` (5.1). `call.ts` is deleted in step B7. |
| 10 | `lib/call.ts:73`: `debounce(saveNow, 300)` | The settings debounce | **Deleted, with no replacement timer.** Each change saves as it happens: `input` for a text field, `change` for a toggle or select, `pointerup` for a divider. One atomic write each (5.9). |
| 11 | `lib/call.ts:102,105`: `pagehide` and `visibilitychange` | Flushing saves on close | **Deleted, with nothing in its place.** Every change was already sent when it was made, so there's nothing to flush at close (5.9). |
| 12 | `lib/composer/Composer.svelte:81`: `queueMicrotask` | Focus and selection after a slash-command pick | **Deleted.** Use `flushSync`, then focus, as `Composer.svelte` already does after a draft change. |
| 13 | `debug.svelte.ts:44,70,94` (signal and socket listeners), `TrackedWebSocket` | Counting sockets for the soak | `TrackedWebSocket` is folded into `socket.ts` (one socket, counted there). The rest is deleted with row 1. |

The single named exception: **none.** There's no debounce in the front end or the backend (row 10, and 5.9).

## 8. Every promise chain in `app/src` today

Each one becomes `async`/`await` inside a `try`, with the owner's signal, in step B9. Where the call moves onto the
socket, the change happens in that phase instead.

| File:line | In | Becomes |
| --- | --- | --- |
| `debug.svelte.ts:30` | `trackedListen` | `await` in an `async` helper. It moves to `host/` with the drag-and-drop listener. |
| `debug.svelte.ts:105` | `debounce` | Deleted (section 7, row 1). |
| `composer/Composer.svelte:61` | `$effect` reading config | An `async` function the effect calls, with the effect's signal. Phase C1 changes it to a `config` subscription. |
| `composer/Composer.svelte:123` | `onDestroy` unlisten | `await` in an `async` cleanup. |
| `Conversation.svelte:93-95` | Agent-name `listAgents` chain | `async` function with a `try`. In phase C1, the name comes from the threads topic summary. |
| `Conversation.svelte:106` | Sender labels (`Promise.all`) | `async` function with a `try`. |
| `Conversation.svelte:265` | `nextFrame` | Deleted (section 7, row 6). |
| `Conversation.svelte:459` | `$effect` before `coverView` | Deleted (section 7, row 7). |
| `routes/+page.svelte:31,48` | `listAgents`, `openThread` | `async` functions with a `try`. |
| `routes/+page.svelte:140` | `logCounters().catch` | Moves with D2. |
| `soak.ts:247,251,357` | The soak | Moves out (section 7, row 4). |
| `call.ts:27,85,86,99` | `send`, `saveNow`, `saveAll` | Deleted with `call.ts` (step B7). |
| `chat/Tool.svelte:28` | `$effect` reading tool detail | An `async` function the effect calls. |

## 9. Guards and lint

### 9.1 `app/scripts/guards.sh`, with no allowlist

`guards-allowed.txt` is deleted. The script names the two places that may do anything special inside its own text,
as constants, with a comment saying they're the only ones:

- `TRANSPORT=app/src/lib/socket.ts`, the only file that may open a `WebSocket`.
- `HOST=app/src/lib/host/`, the only folder that may use Tauri or listen on the window, the document or a media
  query.

Every file in `app/src`, those included, fails on any of these:

- `setTimeout`, `setInterval`, `requestAnimationFrame`, `queueMicrotask`
- Svelte's `tick(`, `AbortSignal.timeout`
- `fetch(`
- `.then(`, `.catch(`, `.finally(`
- a `:has(` in CSS
- `observe(document.body`
- a markdown parser import, anywhere. Today's `lib/editor/preview/` carve-out goes, since there's no editor yet;
  the editor phase (W2) asks for it if it needs one.
- an import from `app/measure` (D2)
- any Tailwind variant that starts with `has-` or contains `-has-`, all of which compile to `:has()`: `has-[...]`,
  `has-data-[...]`, `has-checked:`, `has-disabled:`, `has-aria-[...]`, `not-has-...`, `*:has-[...]`, `group-has-*`,
  `peer-has-*`. The script splits each class on `:` and fails a variant (any part but the last) that starts with
  `has-` or contains `-has-`. Today's guard checks only the text `:has(`, so these slip past it. The registry's `Button`, added in
  91a01e6, already uses `has-data-[icon=inline-end]` and `has-data-[icon=inline-start]`, so B9 edits it (section
  9.1b).
- a looping animation outside a busy state (the loop rule below)

**The loop rule (Jesse's AGENTS line: a loop may run only while real work runs).** It can be checked by text, with
no exception list, because the rule is about where a loop is written, not which file writes it:

- In CSS (`.css` files and `<style>` blocks), `infinite` may appear only inside a rule whose selector includes
  `[aria-busy="true"]` (or `[aria-busy='true']`). The script tracks the selector of the rule it's in, the same way it
  reads `<style>` blocks for `:has(` today.
- In markup, looping utilities pass only with `aria-busy:` or `group-aria-busy:` in front, such as
  `aria-busy:animate-spin`; a bare `animate-spin` fails. The looping names aren't typed into the script: it reads
  them from the CSS that `app/src/app.css` imports, taking every `--animate-<name>` and `@utility <name>` whose
  animation is `infinite`. Today that gives Tailwind's `animate-spin`, `animate-pulse`, `animate-ping` and
  `animate-bounce`, and shadcn-svelte's `shimmer` (`shadcn-svelte/tailwind.css`, 2 s, `infinite`, used by Trace), so
  a package update that adds a loop is caught too. Any `animate-[...]` with `infinite` fails the same way.
- So any file may have a loop, but only one that's off unless the element says it's busy. Today's
  `chat/tools/Plan.svelte` spinner (`.glyph.spin { animation: … infinite }`, set while `running`) becomes
  `[aria-busy='true']`, with `aria-busy={running}`.

What text can't check is whether `aria-busy` really follows real work. That's checked by a test instead: B10 adds a
check to the window run (and to the B9 node test with happy-dom where it can) that `document.getAnimations()` holds
no animation whose `iterations` is `Infinity`, at three moments: after launch, after a turn finishes, and after a
turn is cancelled. The old Assembly's loops stayed on after work stopped, so the last two matter most. A loop left on
at rest fails it, whichever file it's in. In measure mode (D2 A), the measure tools also count live `setTimeout` and
`setInterval` timers and pending `requestAnimationFrame` callbacks at those moments, which catches timers and frame
loops inside packages (dockview-core's popout loop is a frame loop); each count must be zero. F1 adds a fourth
moment: after a pane drag ends.

**The build backstop.** After `vite build`, CI fails if the built CSS contains `:has(`. That also covers CSS from
packages and from a later shadcn-svelte update, which the source scan can't see.

These are checked in every file:

- A `WebSocket` outside `TRANSPORT`.
- `@tauri-apps/` or `listen(` outside `HOST`.
- Outside `HOST`: `addEventListener(` on anything, `<svelte:window`, `<svelte:document`, `matchMedia(`, and
  `MediaQuery` from `svelte/reactivity`.

Under D2 A, `app/measure/` is scanned with the same rules, except that timers and frame callbacks are allowed
there. It's the one place the timer rule doesn't reach.

Its self-test lines, which are today's design, stay. A new probe is added for each new rule.

`TRANSPORT` and `HOST` are the only two names in the guard script. A third needs Jesse's OK. Changing the rules
means changing the script, which is locked, so adding a rule or a place takes an OK in `.pinned-approvals`. Nobody
can add their own file to a list, because there's no list.

### 9.1b Components from the registry and the guard

Components added from the shadcn-svelte or AI Elements registry land in `app/src/lib/components/` like any other
code. The guard scans them with the same rules and no carve-out: no folder is exempt, the registry's included. Some
will fail on add. Each is edited when it's added to meet the rules, and the PR that adds it says what was changed and
why. The ones known now:

| Component pattern | Why it fails | The edit on add |
| --- | --- | --- |
| A copy button that resets "copied" with `setTimeout` | timer | "Copied" stays until the pointer leaves or the next copy; no timer. |
| Clipboard with `navigator.clipboard.writeText(…).then(…)` | `.then`, and the clipboard is a host call | `await` the host's clipboard call in `lib/host/` (the app already uses the Tauri clipboard plugin). |
| Shimmer, pulsing skeletons, `animate-spin` loaders | loop at rest | `aria-busy:` in front, and the component takes a `busy` prop bound to real work (the loop rule). |
| Tailwind `has-*` variants (the `Button` from 91a01e6) | compiles to `:has()` | A data attribute on the element itself, set from a prop (for example `data-icon-end`), styled directly. |
| `document.cookie` or `localStorage` for state (the sidebar's open state) | state belongs in the backend | Saved through `save` (5.9), like every setting. |
| A `window`, `document` or media-query listener in a component (including `<svelte:window>`, `<svelte:document>`, `IsMobile`'s `MediaQuery`) | listeners live in host | The component subscribes to what `lib/host/` exposes (a key, the window size, the system theme). |
| `Collapsible` in a per-row chat component | `bits-ui` runs a tick after each toggle | Per-row chat components use a plain `{#if}`, not `Collapsible`. |
| `mode-watcher` for light and dark | `localStorage` and its own media-query listener | Not used. The theme is one theme id from the registry, saved through `save` (F2). |
| Height classes (`h-9` in the `Button`) | heights are set once in `app.css` | The class names a size from `app.css` (`size-icon-button`, `h-tab`), never a number (F2). |
| Shadow classes (`shadow-xs`, `shadow-md`) | the design has no shadows | Removed on add. `--shadow-*` is cleared in `app.css`, so a missed one draws nothing. |

Never run a shadcn.io install. Those items are built on `@ark-ui/svelte` and would overwrite the `bits-ui` parts in
`components/ui/`, so their markup is copied onto `bits-ui` instead. The install command carries Jesse's personal
token, so it never goes into the repo, a note or a log.

`bits-ui` arrives with the first component that needs it. It's pinned to the exact version installed, like the
CLI, and so is every package a component brings.

Timers inside the packages themselves (`bits-ui`'s tooltip delay, `Collapsible`'s tick after a toggle,
`dockview-core`) are in `node_modules`, which the guard doesn't scan. Whether to accept them is Jesse's call
(section 12). The at-rest checks (9.1) are the net: they catch a package loop or timer still running when nothing is.

`guards.sh` and `guards-allowed.txt` are locked on main now (`scripts/locked-paths.txt`), since PR #1 merged.
**Step B10 needs Jesse's OK lines** (section 9.3), because the lane that wants the change can't OK it (`AGENTS.md`,
"Who may OK").

### 9.2 `scripts/lint.d/`

- `00-typescript-only.sh` stays as it is.
- `40-routes.sh` guards today's HTTP `Map()` pattern, which goes away. It's replaced by
  `40-one-dispatcher.sh`. Like `guards.sh`, it names the only places that may do each special thing, once each, as
  constants in the script, with a comment saying they're the only ones:
  - `SOCKET=Assembly.Infrastructure/Socket/SocketEndpoint.cs`, the one file that maps the socket and accepts a
    WebSocket;
  - `MCP=Assembly.Infrastructure/Mcp/McpEndpoint.cs`, the one file that maps `/mcp`;
  - `DATABASE=Assembly.Infrastructure/Persistence/`, the one folder that opens a `SqliteConnection`;
  - `WRITER=Assembly.Infrastructure/Persistence/AtomicWriter.cs`, the one file that begins a transaction.

  Everywhere else under `dotnet/`, outside `Assembly.Tests`, it fails on `MapGet(`, `MapPost(`, `MapPut(`,
  `MapDelete(`, `.Map(`, `MapGroup(` and `AcceptWebSocketAsync(` (outside `SOCKET` and `MCP`),
  `new SqliteConnection(` (outside `DATABASE`), and `BeginTransaction(` (outside `WRITER`). Those four are the only
  names in the script; a fifth needs Jesse's OK.
- **`40-routes.sh` is deleted in step B7,** together with `FeatureCalls.cs`. `scripts/lint.d` isn't locked on main,
  so that needs no OK.
- **There's no route check between B7 and the end of C1.** That's a stated gap, not a list. Reviews cover it: a new
  `Map` call in that window is a finding.
- **`40-one-dispatcher.sh` lands at the end of phase C1 with zero exceptions,** in the commit that deletes the last
  chat HTTP route. It has no legacy list, and no list that can only shrink.
  - **Why then, and not inside the base:** moving the chat routes onto the socket before B11 would put all of
    phase C1 into the base. That would roughly double the base, and delay Jesse's review of the pattern it's meant
    to show.
- **`scripts/lint.sh` on main is still the stub,** so no `lint.d` check runs anywhere today. Step B10a, before B11,
  replaces it with the move thread's `lint.sh` that runs every `lint.d` script (blob `ac101c9`). `lint.sh` is locked,
  so that needs an OK line by the move thread (section 9.3).
- New checks, all run by `scripts/lint.sh`:
  - `50-dotnet-format.sh`: `dotnet format --verify-no-changes`.
  - `60-svelte-check.sh`: `svelte-check`, with zero errors.
  - `70-architecture.sh`: runs `ArchitectureTests`.
  - Once Jesse decides the size limit, a size check.
- **When the lint configs get locked:** after step B2 (the move) and B11 (the new checks) are on main, step B12 adds
  `scripts/lint.d/**`, `dotnet/.editorconfig`, `dotnet/Directory.Build.props` and `dotnet/Directory.Packages.props`
  to `scripts/locked-paths.txt`. Before that, the move changes those files at every step, and locking them earlier
  would need an OK each time.

### 9.3 The OK lines the base needs

| Step | Change | OK by |
| --- | --- | --- |
| B10a | Replace `scripts/lint.sh` with the version that runs `lint.d` (blob `ac101c9`) | `by:move-thread` |
| B10 | Change `app/scripts/guards.sh` | `by:jesse` (his 18:11 message on 26 October asks for it) |
| B10 | Delete `app/scripts/guards-allowed.txt` | `by:jesse` |
| B10 | Drop `guards-allowed.txt`'s line from `scripts/locked-paths.txt` | `by:jesse` |
| B12 | Add the new locks to `scripts/locked-paths.txt` | `by:move-thread` |

All of these wait until Jesse removes the three settings denies that block writing `.pinned-approvals`.

## 10. The base, commit by commit

All of this happens on one branch, `lane/base`, with each step a commit. Each code commit:

- has its failing test written first, and seen failing, before the code;
- runs the full `scripts/check4.sh` on the pushed commit, with the evidence committed alongside;
- is reviewed by the move thread before the next step starts.

**One PR per step group:** B1; B2; B3 to B6; B7 and B8; B9 to B12. Each PR opens as a draft when its group is done,
and is marked ready once its proof is in. It has a plain summary written for Jesse, saying what to read first and
what isn't proven yet. Jesse merges each one, and the next group builds on main after he does.

### B1. Merge the AGENTS.md files

Jesse pasted mac-command-bar's `AGENTS.md` at the top of this repo's in 91a241d. B1 adapts that paste to this repo
as section 3 says, and adds the shadcn-first rule (F2).

- **Files:** `AGENTS.md`.
- **Test:** none (documentation).
- **Proof:** the move thread and Jesse read the diff.
- **Size:** about 250 lines changed.

### B2. Move to the Clean Architecture layout (no behavior change)

B2 is three commits, so each can be read on its own. Before the move, today's projects get the plain-SQL contracts,
because chat's row and thread stores and the preference store call SQLite directly, and Application may not.

- **B2a. Plain SQL, inside today's projects, before anything moves.**
  - **Files:**
    - New contracts: `IAppDatabase`, `IWriteScope`, `IRow`, `SqlArg` and `IAtomicWriter`, with the signatures in
      5.6.
    - `AppDatabase`, one per database file: reads on pooled read connections, one per read, cancellable (5.6). It
      owns `AtomicWriter.cs`, the one file that begins a transaction from here on: one long-lived write connection
      behind one lock, held for the whole write, with one transaction per write (begin, run the body, commit, or roll
      back). There's no audit, queue or prune yet; B3 adds those to this same class. There's no second write path,
      and nothing in B2a that B3 has to delete.
    - `RowStore`, `ThreadStore` and `PreferenceStore` rewritten as plain SQL: reads through `IAppDatabase`, writes
      through `IAtomicWriter.Write`. Their public methods stay the same, so their callers don't change. They're opened
      from the `AppDatabase` instead of a path, so a rows batch and a thread write share the one write lock: the hosts
      open one `AppDatabase`, and the tests change only their setup line.
  - **Failing tests first,** for the contracts: a query maps rows by position; a write that throws leaves nothing;
    a write that succeeds is there after it commits; a rows batch and a thread write run at the same time and both
    commit, with no `SQLITE_BUSY`; a long read that's cancelled returns early, cancelled.
  - **Measured before and after the rewrite, on agent-workbox:** a 20-row batch commit through the real `RowStore`, a
    50-row `RowsAfter` read, a 50-item `PageBefore` read, a 50-thread list read, and the thread list while a page
    read runs on another thread (each median and 99th percentile), and the Native AOT backend's idle memory. The numbers go in PR 2's text. If either
    gets worse, that's a defect, fixed before B2b.
  - **Proof:** every existing test, the pinned ones included, passes unchanged. That's the proof behavior didn't
    change.
- **B2b. `ArchitectureTests`, failing.** `Assembly.Tests/Architecture/ArchitectureTests.cs`. It loads each assembly
  and checks its references, and reads the source tree for the rest (no new package):
  - `Assembly.Domain` references no `Assembly.*`;
  - `Assembly.Application` references only `Assembly.Domain`;
  - neither `Assembly.Domain` nor `Assembly.Application` references `Microsoft.Data.Sqlite` or ASP.NET Core, or
    names `System.Diagnostics.Process` (so starting a process stays in Infrastructure, now that `SharedAdapter` and
    the ACP code are in Application);
  - `Assembly.Infrastructure` doesn't reference `Assembly.Mac` or `Assembly.Remote`;
  - no file under `Application/Features/<A>/` names the namespace `Assembly.Application.Features.<B>` for another
    feature `B` (in a `using` or written out), and every file's namespace matches its folder, so the check can't be
    dodged;
  - (the page's rule, that no file under `app/src/lib/<feature>/` imports from another feature's folder, comes in C1;
    see there);
  - no folder under `dotnet/` or `app/src/` (outside `bin/`, `obj/` and `node_modules/`) holds more than 30 files,
    counting the files directly in it, not its subfolders. The number is one constant in the test, and there are no
    exceptions. Today's largest folder, `Assembly.Engine/Acp/Messages/`, has 29.

  It fails because the projects don't exist yet.
- **B2c. The move.** Git shows it as renames, with only namespace and `using` edits in them. PR 2's text carries
  the proof, so Jesse can read B2a and skip the moved files: `git diff -M --stat` and `--summary` for the commit,
  showing every moved file as a rename, and a check that every changed line in those renames is a `namespace` or
  `using` line (or a type name written out with its namespace). New files (the project files) are listed apart.
  - **Before it:** one commit with the code changes the move needs: the stores take `IAppDatabase`, which hands out
    the file's `IAtomicWriter`, so nothing in Application names `AppDatabase`.
  - **After it:** one commit that keeps the file system out of the core (`TranscriptLog` takes a writer, and
    `AgentDescriptor` parses text; the hosts and Infrastructure open the files), with `ArchitectureTests` banning
    the file-system types in Domain and Application.
  - **New:** `dotnet/Assembly.Domain/Assembly.Domain.csproj`, `dotnet/Assembly.Application/Assembly.Application.csproj`
    and `dotnet/Assembly.Infrastructure/Assembly.Infrastructure.csproj`, and `dotnet/Directory.Packages.props`. The
    `DependencyInjection.cs` files and the hosts' `Add*` calls come in B7, with the dispatcher that registers
    features. Until then the hosts build their objects as today, so the move stays renames.
  - **Moved:** every file in the "Where today's folders go" table in section 4. Today's tests go to
    `Assembly.Tests/Features/<Feature>/`, `Core/` or `Infrastructure/`, matching the code they test; `Draft/`,
    `Pinned/` and `Fixtures/` stay where they are.
  - **Deleted:** `dotnet/Assembly.Engine/`.
  - **Changed:** the hosts' and tests' project files, `Assembly.slnx`, and the files that name `Assembly.Engine/*` and
    `Assembly.Mac/*` paths: `scripts/lint.d/40-routes.sh`, `scripts/check4.sh`, `AGENTS.md`, `dotnet/README.md` and
    a few comments. `scripts/locked-paths.txt` gains `dotnet/Assembly.Tests/Architecture/**`. `AGENTS.md`'s provider line names one folder for provider quirks:
    `Application/Common/Acp/V1/`. That's where the adapters' updates are read, and where today's provider
    differences already are (`ClaudeBackground`, `CodexThreadStatus`, `UpdateReader`). `Infrastructure/Agents/` only
    starts processes, so a quirk there would be about launching, not about what a provider sends.
  - **Changed:** `scripts/check-evidence.sh`. Its list of code folders names `dotnet/Assembly.Engine`; that's
    replaced by `dotnet/Assembly.Domain`, `dotnet/Assembly.Application` and `dotnet/Assembly.Infrastructure`, or
    backend commits would skip the four-way check without a word. The script also fails when a `dotnet/Assembly.*`
    project folder is neither in the list nor `Assembly.Tests`, so a project added later can't be skipped. It isn't
    locked.
  - `ArchitectureTests` passes.
- **Proof:**
  - every existing test passes after B2a and again after B2c (201 passed and 4 skipped of 205 at `7805f12`, plus the
    new tests);
  - check4 passes on all four cases, on B2c;
  - `shell/assembly.sh build` publishes AOT;
  - `check-evidence.sh` fails on a range holding a B2 commit with no evidence, and on a scratch
    `dotnet/Assembly.Scratch/` folder (removed afterwards);
  - the move thread sees the app run in the window on the Mac. This is owed, not done: builds on Jesse's Mac are
    paused until he says so, and PR 2's text says so.
- **Size:** B2a about 300 to 500 lines with its tests. B2b about 150 lines. B2c is mechanical (namespaces and
  `using` lines across about 250 files) plus about 150 lines of project files and DI.

### B3. One write path: `IAtomicWriter` and the audit

- **Files:**
  - Application: the contracts in `Common/Database/` exist from B2a; B3 doesn't change their shape.
  - Infrastructure: `Persistence/AtomicWriter.cs`, which B2a made, gains the audit row and the prunes. The audit
    row records `what` and the time; B4 adds the connection id as one more column. The per-connection write queue
    comes with the socket in B4. New: `Persistence/AuditSchema.cs`, `Persistence/SchemaRunner.cs`.
  - Chat: `Features/Chat/Rows/` and `Features/Chat/Threads/`. Their SQL already goes through the contracts (B2a); B3
    splits today's `RowStore` and `ThreadStore` into chat's own named queries, so neither stays a class that wraps a
    table.
- **Failing tests first,** in `Assembly.Tests/Infrastructure/Persistence/AtomicWriterTests.cs`, on a real temporary
  SQLite file:
  1. A write whose second statement throws leaves neither row, and publishes nothing.
  2. A successful write leaves its rows, one audit row, and its publish, which is sent after commit (the test
     subscribes and checks the order).
  3. A failing commit (an injected failure) leaves nothing and publishes nothing.
  4. Two writes at once run one after the other.
  5. A chat row batch commit goes through the writer: its rows and one audit row commit together.
  6. Startup prune, with `FakeTimeProvider`: a database with audit rows 31 days old and 29 days old keeps only the
     29-day ones after the writer opens.
  7. Long-running prune: with no restart, advancing fake time 31 days and then writing deletes the expired rows. A
     second write the same day doesn't prune again.
  8. The triggering write comes first: with the test subscribed to the write's topic and a hook on the prune's
     transaction, the write's commit and its publish both happen before the prune's transaction begins, and the
     prune is a separate transaction (a failure injected into the prune leaves the triggering write committed).
  9. A backlog over the cap clears over the following writes: with 12,000 expired rows, the first write's prune
     deletes 5,000, the second write's 5,000 and the third write's 2,000; a fourth write the same day doesn't prune.
- **Also:** chat's row and thread writes move onto `IAtomicWriter` (5.6), and `scripts/lint.d/45-no-select-star.sh`
  fails on `SELECT *` under `dotnet/` (rows are read by column position).
- **Measure:** a 20-row batch with its audit row, through the real writer, on agent-workbox, with the median and
  99th percentile recorded beside the scratch numbers in 5.6, so chat doesn't slow.
- **The commit's slow tail.** Before B2a, a 20-row commit took about 0.4 ms at the median but about 20 ms at the 99th
  percentile. B3 finds the cause (most likely WAL checkpoints, which SQLite runs inside the commit that crosses
  1,000 pages) by timing commits with and without automatic checkpoints, and says what's done about it. A 20 ms
  stall inside the one write lock also holds up a keystroke save waiting behind it, so the fix keeps checkpoints out
  of the write lock (for example `wal_autocheckpoint` off on the write connection, with a checkpoint run after a
  write releases the lock). Proof: the p99 before and after.
- **Proof:** the tests, run, with their counts, the measurement, and check4.
- **Size:** 250 to 350 lines with tests.

### B4. The socket: endpoint, frames, dispatcher, cancellation

- **Files:**
  - Application: `Common/Calls/ICallRegistry.cs`, `CallContext.cs`, `CallFailed.cs`, `IFeature.cs`.
  - Infrastructure: `Socket/SocketEndpoint.cs`, `SocketConnection.cs` (one receive loop, one sender, a bounded
    queue), `SocketDispatcher.cs`, `SocketFrames.cs`, `SocketJson.cs`.
  - Page: `app/src/lib/socket.ts` (`call`, `onFailure`), and `app/src/lib/host/backend.ts` (`backend-ready`).
  - Shell: `shell/src/main.rs` sends `backend-ready`.
- **Failing tests first,** in `Assembly.Tests/Infrastructure/Socket/SocketTests.cs`, over a real loopback socket:
  1. A call answers `result`, and an unknown one `not_found`.
  2. Bad args answer `bad_args`.
  3. A wrong token closes with 4401, and a wrong protocol with 4400.
  4. `cancel` fires the handler's token (base#10).
  5. Closing the socket cancels running calls.
  6. Two calls at once each get their own answer.
  7. A handler that throws answers `failed` and logs at Error.
  8. A call over 30 s answers `timeout`, using `FakeTimeProvider` with no real waiting.
  9. A frame over 4 MB closes the socket.

  On the page, `app/test/draft/base/socket.test.ts`, against a fake socket:
  1. Latest-wins by key sends `cancel` for the older call.
  2. The owner's signal sends `cancel`.
  3. A close fails pending calls as `aborted`.
  4. One reconnect, then waiting for `backend-ready`.
- **Proof:** the tests. check4 passes, plus a new scenario, `scripts/features/socket.ts` (node, since curl has no
  WebSocket on the Mac), which makes a call and a cancel on both machines.
- **Size:** 600 to 800 lines with tests.

### B5. Topics and the backend clock

- **Files:**
  - Application: `Common/Topics/ITopics.cs`, `Common/Clock/IBackendClock.cs`.
  - Infrastructure: `Topics/Topics.cs`, `Clock/BackendClock.cs`.
  - Page: `socket.ts` gains `subscribe`.
- **Failing tests first:**
  1. Subscribing sends `state`, then each `change` published after it.
  2. Unsubscribing, or closing the socket, stops changes.
  3. The clock ticks only while subscribed (`FakeTimeProvider`).
  4. A `minute` subscriber gets exactly one change per minute over three fake minutes, while a `second` subscriber
     on the same clock gets 180.
  5. Subscribing again after a reconnect gives the current state, not a replay.

  On the page, a `subscribe` test applies `state` and `change` to a `$state` map.
- **Proof:** the tests, and check4.
- **Size:** 300 to 400 lines with tests.

### B6. Saves as each change happens

- **Files:**
  - Application: `Common/Calls/ICallRegistry.Save`.
  - Infrastructure: the dispatcher runs `Save` handlers inside `IAtomicWriter`, from the connection's write queue
    (5.9).
  - Page: `socket.ts` gains `save`.
  - No shell change.
- **Failing tests first:**
  1. One `save` makes one write, with one audit row (key, not value), and one publish after commit.
  2. 50 rapid saves of one key, sent without waiting for answers, end on the last value.
  3. A slow write doesn't hold up a read on the same connection.
  4. A failed save reports once, and leaves the committed value as it was.

  On the page:
  1. `save` sends one call per `input` on a text field.
  2. The echo of its own save is a no-op; another window's echo is applied.
- **Measure:** time a single-row settings upsert through the real `IAtomicWriter`, with its audit row, on
  agent-workbox. Record the median and 99th percentile, as 5.9 did for the scratch program (17 µs and 81 µs).
- **Proof:** the tests, the measurement, and check4. In the window on the Mac, the move thread types in a settings
  field and quits right away, and the value is kept after reopening.
- **Size:** 200 to 300 lines with tests.

### B7. Preferences rebuilt on the base (the example every feature copies)

The first port done files first, then code: B7a creates every file below with no logic, B7b brings the code over.
Each is reviewed on its own.

- **Files:**
  - One folder, `Application/Features/Preferences/`: `PreferencesFeature.cs` (its calls, topic and SQL, 5.2),
    `Preference.cs` (the record and its row reader), `PreferencesSchema.cs`, `PreferencesJson.cs`, and `README.md`
    (moved from `Assembly.Mac/Preferences/README.md`, which isn't locked). Nothing for preferences in Domain or
    Infrastructure.
  - Page: `app/src/lib/preferences/preferences.svelte.ts` (subscribe and save), and its `README.md`.
- **Deleted:** `app/src/lib/call.ts`, `dotnet/Assembly.Engine/Features/FeatureCalls.cs`, `PreferencesRoutes.cs`,
  `PreferenceStore.cs` (no reader or writer class replaces it), the `/preferences/*` HTTP route, and `scripts/lint.d/40-routes.sh`, which isn't locked, so
  no OK is needed. From here until the end of C1 there's no route check (section 9.2).
- **Changed:** `AGENTS.md`, which names `call.ts`, `FeatureCalls.cs` and `40-routes.sh`.
- **Failing tests first,** in `Assembly.Tests/Features/Preferences/`:
  1. base#7: a theme change is written when it's made, and is there after the backend restarts.
  2. Two windows: a change in one reaches the other's subscription.
  3. A subscription that starts after a save gets the saved value.

  The page tests change with it. `app/test/draft/base/call.test.ts` becomes `socket.test.ts`, and
  `preferences.test.ts` is rewritten for the subscription.
- **Proof:**
  - check4 passes, with `scripts/features/preferences.sh` rewritten as `preferences.ts` over the socket;
  - the move thread sees, in the window on the Mac, a theme change that survives a quit, and a second window that
    follows it.
- **Size:** 300 to 450 lines, offset by about 400 deleted.

### B8. The Mac's page calls onto the socket

- **Files:** one folder, `Application/Features/Diagnostics/`: `DiagnosticsFeature.cs` (memory sample, App Nap begin
  and end, dropping the link for the debug control) and its small classes. Infrastructure gets only what calls the
  operating system, if anything does.
- **Deleted:** `Assembly.Mac/Page/PageRoutes.cs`, `Assembly.Mac/Debug/DebugRoutes.cs`, and
  `app/src/lib/host/pageCall.ts`.
- **Moved:** the thumbnail calls go to phase C1, with attachments.
- **Failing tests first:** one per call, over the socket test helper from B4.
- **Proof:** check4, and the App Nap and memory reading working in the window on the Mac.
- **Size:** 150 to 250 lines.

### B9. The page with no timers and no promise chains

This step makes every row of section 7 and section 8 that isn't moved to a later phase true. It's one commit per row
group:

1. reconnect (row 5);
2. the reveal (row 6);
3. `coverView` (row 7);
4. the turn clock (row 8);
5. the Composer microtask (row 12);
6. lag, counters and soak, as Jesse decided in D2 (rows 2 to 4);
7. the promise chains.

**Failing test first, for each:**

- Turn clock: the elapsed time follows `clock` changes.
- Reveal: streamed text shows as rows arrive, with nothing waiting on a frame.
- `coverView`: a `ResizeObserver` callback runs it. This is a logic test of the function on fake measurements, not a
  test of the layout.
- Reconnect: `socket.test.ts` (B4).

**Proof:**

- check4 passes.
- The move thread runs the lag meter on the Mac, in measure mode under D2 A, before and after, and posts both.
- A long chat streams and scrolls without blank rows, in the window.
- For `coverView` on `ResizeObserver` (row 7), the window check scrolls a long chat with folds opening, and watches
  the console for "ResizeObserver loop" errors. There must be none.

**Size:** 300 to 500 lines changed, mostly deleted.

### B10. The guard with no allowlist

- **Files:** `app/scripts/guards.sh`, and deleting `app/scripts/guards-allowed.txt`. Both are locked, so this
  **needs Jesse's OK line.** `AGENTS.md`, which names `guards-allowed.txt` and the per-row tick rule (no longer needed
  once no file may tick); and the CI step for the build backstop (9.1).
- **Test first:** new self-test probes for each new rule (`.then(`, `queueMicrotask`, `AbortSignal.timeout`, `fetch`
  in `socket.ts` itself, a `WebSocket` outside `socket.ts`, a Tailwind `has-data-[…]`, a bare `animate-spin`, an
  `infinite` in a rule without `[aria-busy="true"]`, a bare `shimmer`, `has-checked:`, `not-has-[...]`,
  `<svelte:window` and `matchMedia(` outside host, and `addPopoutGroup(`, since popout windows bring dockview-core's
  endless frame loop). Each fails against today's script. Probes that must pass: `aria-busy:animate-spin`,
  `group-aria-busy:shimmer`, and `infinite` inside `[aria-busy='true']`. One more probe: when the CSS in
  `node_modules` that the looping names are read from is missing, `guards.sh` fails instead of passing with an empty
  list.
- **The at-rest check:** `document.getAnimations()` holds no animation with `iterations` of `Infinity` after launch,
  after a finished turn and after a cancelled turn (9.1). It runs in the window check on the Mac. In measure mode,
  the live timer count and the pending frame callback count are zero at the same three moments.
- **Proof:** `guards.sh` passes on the tree after B9, and fails on each probe.
- **Size:** about 120 lines.

### B10a. Turn on `scripts/lint.sh`

- **Files:** `scripts/lint.sh`, replaced with the move thread's version that runs every `scripts/lint.d` script
  (blob `ac101c9`). It's locked, so this needs an OK line by the move thread (section 9.3).
- **Test first:** a scratch `lint.d` probe script that always fails makes `lint.sh` fail. Today's stub passes it.
- **Proof:** `lint.sh` runs `00-typescript-only.sh`, and fails on the probe.
- **Size:** a few lines.

### B11. The lint checks

- **Files:** `scripts/lint.d/50-dotnet-format.sh`, `60-svelte-check.sh`, `70-architecture.sh`.
  `40-one-dispatcher.sh` lands at the end of phase C1 instead (section 9.2).
- **Test first:** each script gets a probe file it must fail on, run from a scratch copy, as for the guard.
- **Proof:** `scripts/lint.sh` passes on the tree, and each probe fails.
- **Size:** about 200 lines.

### B12. Lock the configs, then open the PR

- **Files:** `scripts/locked-paths.txt` adds `scripts/lint.d/**`, `dotnet/.editorconfig`,
  `dotnet/Directory.Build.props`, `dotnet/Directory.Packages.props`, and, under D2 A, `app/measure/**`. That file
  is itself locked, so this needs an OK line by the move thread (section 9.3).
- **Proof:** `scripts/check-pinned.sh` refuses a test edit to each newly locked file.
- **Then:** the B9 to B12 PR is marked ready, as the last of the five PRs (section 10).

## 11. The later phases

Each phase follows the base's pattern: one folder, `Application/Features/<Feature>/`, with its `<Feature>Feature.cs`,
records, rules and SQL; its tests in `Assembly.Tests/Features/<Feature>/`; its store in `app/src/lib/<feature>/`; a check4 scenario, and the old route deleted in the same phase. Each starts by
drafting its behavior list (`docs/behavior/<feature>.md`) for Jesse to correct. Then, as from B7 on (AGENTS.md,
"Files first, then code"), each port is two reviewed steps: every file in its place with no logic, then the old
code brought over piece by piece. Sol's step numbers are given so his
catalogue stays the reference.

### C. Chat on the socket

- **C1. Threads, rows, sends and config** (Sol P03, P07, P12, P14).
  - **Files:**
    - `Features/Chat/Threads/ThreadsFeature.cs`, with its topic: one summary per thread (state, active run, pending
      ask, needs attention, background work, title, machine, online, project, last activity, unread);
    - `Features/Chat/Rows/RowsFeature.cs`, the `rows` topic with `after`;
    - `Features/Chat/Sessions/SendFeature.cs` (send, stop, answer, stop task), and
      `Features/Chat/Config/ConfigFeature.cs` with a `config` topic;
    - the page's chat folders, moved under `app/src/lib/chat/` (section 4).
  - **First, the page's layout rule, after B9 and B10.** B10 deletes the guard's allowlist and B9 removes the waits,
    so chat's page folders can move without changing what the guard's per-row zone (`lib/chat/`) means. In this
    order: `ArchitectureTests` gets back `No_page_feature_imports_another` (no file under `app/src/lib/<feature>/`
    imports another feature's folder; written first, failing; the test file is locked by then, so the move thread
    posts the OK line); then `composer/`, `rows/`, `thread/` and `Conversation.svelte` move under `app/src/lib/chat/`
    as renames with import-path edits, and the test passes. B2b had it, and B2c took it out: today those folders
    import each other.
  - **Named item: the page read on thread switch.** `PageBefore` takes about 200 ms for 50 items on a 40,000-row
    thread (197 to 205 ms median, 294 ms p99, measured on agent-workbox before B2a). That's very likely the "slow
    frames only on thread switch" from the Mac lag run. Run `EXPLAIN QUERY PLAN` on it, then add an index or change
    the query so a page loads in single-digit milliseconds, with before and after numbers.
  - **Reads take the call's token.** Since B2a every store read passes `CancellationToken.None`, because the
    signatures didn't change, so no load in the backend can be cancelled yet. When the chat reads move onto the socket,
    they take the call's token, starting with `PageBefore` alongside its index fix.
  - **Deleted:** `Infrastructure/Http/*` chat routes, `LiveSocket.cs`, `app/src/lib/api.ts`,
    `app/src/lib/rows/client.ts`. The last commit adds `scripts/lint.d/40-one-dispatcher.sh`, with zero exceptions
    (section 9.2). Adding a file to the locked `lint.d` needs no OK.
  - **Tests:**
    - base#8: a thread made while the page is subscribed arrives as an upsert;
    - base#9: the pending run is shown during the gap before the copied row arrives (the failing test is written
      before the fix), and the menus come from a host started with an empty home;
    - the rows-topic catch-up and `prevSeq` tests that exist today, moved;
    - attachments as chunked calls (D4): a 10 MB image arrives in about 10 calls and commits once; cancelling
      midway leaves nothing linked.
  - **Proof:** check4 with `rows.ts`, `threads.ts` and `config.ts`, and the chat used in the window on the Mac.
- **C2. One socket per remote machine** (Sol P11, P13).
  - **Files:** `Infrastructure/Remote/RemoteMachine.cs` becomes a socket client of the remote's `/socket`, and
    `RemoteFollower.cs` becomes one subscription per thread on that socket. The remote's threads topic is relayed,
    which brings threads made on the remote into the list (base#11, Jesse's call). `RemoteOutbox.cs` polling becomes
    a subscription.
  - **Tests:**
    - a relayed upsert for an unknown remote thread shows in the list;
    - a disconnect marks the machine offline;
    - a reconnect catches up without duplicates.
  - **Proof:** check4 on Workbox Test.
- **C3. MCP servers** (Sol P09).
  - It starts by finding out why the old app didn't have the MCP problem (Jesse's comment), and changes nothing until
    that's answered.
  - **Files:** decided by what the investigation finds.
  - **Proof:** the process tree under Claude Code and Codex, before and after.
- **C4. History, helpers, background work, native subagents** (Sol P08, P16, P26).
  - **Files:** `Features/Chat/Sessions/*` and `Features/Chat/Helpers/*`, on the base.
  - **Tests:** Sol's P08 and P16 test groups.
  - **Proof:** check4, plus the helper scenario.

### F. Frame and Look (the visible shell)

- **F1. The frame** (Sol P17, P18): sessions, chat, the right drawer, the bottom dock (base#4, base#5). Jesse's
  direction (2026-10-26): panes drag anywhere, and a divider drags as far as the next pane. The fixed grid is
  replaced by a docking layout, most likely `dockview-core`, the framework-free build of dockview, with each pane's
  body a Svelte component mounted into it. The package waits for Jesse's OK (section 12).
  - **Files:** `app/src/lib/shell/` (the dock, one file mounting panes into it), with the layout saved through
    `save` (5.9).
  - **Saving:** the layout (`toJSON()`) is saved once at the end of a drag, per base#7: when a divider is let go
    (pointerup) or a pane is dropped. It's one atomic write, never one per frame of the drag.
  - **Tests:** only the rules, as logic: a saved layout loads back the same, and a drag sends one save at its end.
  - **At rest:** the checks in 9.1 also run after a pane drag ends: no endless animation, and in measure mode no live
    timer and no pending frame callback.
  - **Proof:** a feature check in the window, with screenshots of panes moved and dividers dragged to the next pane.
- **F2. The look** (base#6): mac-command-bar's theming system, ported, with the values from Jesse's design system.
  - **Decided by Jesse, 2026-10-26:**
    - 20:08Z: "No, i want the system fonts for sure. That's how any app presents fonts, you choose your own."
    - 20:09Z: "Really, just mostly use the old system, because then it maps to everything in the system i'm thinking."
    - 20:10Z: "There's a light theme. There's a number of them. Just not a specific light theme i guess. Not like a toggle
      between only two."
  - **Sources.** The old system is mac-command-bar at 2089290, `tauri-svelte-preview/src/lib/`, read from the checkout
    on agent-workbox:
    - the token files `styles/tokens.css`, `shell/styles/nextTokens.css` and `shell/styles/themeChrome.css`;
    - in `shell/settings/themes/`: `themeRegistry.ts`, `themeService.ts`, `fontRegistry.ts` and `fontService.ts`.

    The values come from Jesse's design system "Assembly" (https://claude.ai/artifact/8RxzC2U8koy3wAKjRZKkt5, made
    2026-10-23 from mockups D, E and F, https://claude.ai/artifact/EWzSRsmW7i33gEzL9cy3GG). Its `tokens.json` and
    `README.md` are copied verbatim into `docs/design/`. Sol's section 9 ("Design-system integration" in
    `docs/# Assembly .md`) isn't the source. It's cited only where it agrees: one token set, and no web fonts.
    Its space-name collision doesn't apply, because the old `tokens.css` isn't in this repo.
  - **One token set.** The three old files become one set on `:root`. The old app needed `.next-shell` and
    `:root.next-shell-document` because it had two pages; this app has one, so there's no double scoping. The set
    has three kinds of names:
    - Where Tailwind or shadcn already has a name with the same meaning, that's the one name. This covers
      `--color-border`, `--color-primary`, `--font-sans` (the old `--font-ui`), `--font-mono`, `--radius-*` and
      `--text-*`.
    - Where only the old system has a name, the old name comes over as is, for example `--color-rail`,
      `--color-text-2`, `--color-hover`, `--color-live` and `--menu-sheet-surface`.
    - Where the two disagree, the old name gives way. The one case is accent. shadcn's `accent` is the hover fill
      (menu items, ghost buttons); the old `--color-accent` was the brand colour. So `--color-accent` means shadcn's
      hover fill, and the periwinkle is `--color-periwinkle`, beside `--color-aqua` and `--color-lavender`.

    The old names go in one `@theme static` block in `app/src/app.css`, holding the Assembly theme's values. That
    makes them Tailwind utilities (`bg-rail`, `text-text-2`), and it paints the first frame before any script runs,
    so there's no flash. shadcn's names go in `@theme inline`, each one an old name: `--color-background:
    var(--color-bg)`. shadcn's bare `--accent`, `--border` and the rest, its light `:root` block, `.dark` and
    `@custom-variant dark` are removed. Each meaning has one name.
  - **The design system's roles on the old names.** Grounds and text:

    | Design system | Value | Old name it sets | shadcn names that read it |
    | --- | --- | --- | --- |
    | surface | `#18181b` | `--color-bg`, `--color-surface` (one ground: panels are flush) | `background`, `card` |
    | rail | `#19191c` | `--color-rail` | `sidebar` |
    | field | `#1f1f24` | `--color-elevated`, `--color-hover` | `muted`, `accent` (the hover fill) |
    | raised | `#212125` | new `--color-raised`; `--composer-surface` reads it | |
    | chip | `#232328` | new `--color-chip` | `secondary` |
    | selected | `#26262b` | `--color-selected` | |
    | selected-strong | `#2a2a30` | new `--color-selected-strong` | |
    | line | `rgba(236,236,240,0.12)` | `--color-border`, `--color-field-border` | `border`, `input` |
    | line-soft | `rgba(236,236,240,0.06)` | new `--color-line-soft` | |
    | ink | `#e4e4ea` | `--color-text` | `foreground` |
    | ink-body | `#d4d4dc` | new `--color-ink-body` | |
    | ink-2 | `#c4c4cc` | new `--color-ink-2` | |
    | ink-muted | `#9b9ba6` | `--color-text-2`, `--color-idle`, `--color-status-idle`, `--color-section-header-text` | `muted-foreground` |
    | ink-faint | `#8e8e99` | `--color-text-3`, `--color-disabled-text` | |
    | ink-tab | `rgba(228,228,234,0.7)` | `--color-tab-unfocused-text` | |
    | ink-icon | `rgba(228,228,234,0.85)` | new `--color-ink-icon` | |

    Accent, states and the rest:

    | Design system | Value | Old name it sets | shadcn names that read it |
    | --- | --- | --- | --- |
    | primary, on-primary | `#d6d6da`, `#16161a` | new `--color-primary`, `--color-on-primary`; `--color-on-accent` takes on-primary's value | `primary`, `primary-foreground` |
    | accent (periwinkle) | `#8b93ff` | `--color-periwinkle` (was `--color-accent`), `--color-focus-solid`, `--color-selected-border`, `--color-section-focus-ring` | `ring` |
    | focus-line | `rgba(139,147,255,0.35)` | `--color-focus` | |
    | aqua, lavender, code-bg | `#7ee3e0`, `#b79cff`, `rgba(183,156,255,0.1)` | new `--color-aqua`; lavender also sets `--color-merged`; new `--color-code-bg` | |
    | mint | `#7fdcb0` | `--color-good` | |
    | blue | `#7ec8ff` | `--color-live` | |
    | amber, attention-bg, attention-line | `#e6c97e`, `rgba(230,201,126,0.12)`, `rgba(230,201,126,0.34)` | `--color-attention`, `--color-attention-bg`, new `--color-attention-line` | |
    | red | `#ec8b86` | `--color-bad` | `destructive` |
    | add-bg, del-bg, hunk-bg, hunk-ink | as in `tokens.json` | new `--color-add-bg`, `--color-del-bg`, `--color-hunk-bg`, `--color-hunk-ink` | |
    | provider-orange, provider-blue | `#c98a6c`, `#7f9fd6` | `--agent-mark-claude`, `--agent-mark-codex` | |
    | file-svelte | `#ff6a3d` | new `--color-file-svelte` | |
    | (none) | | `--color-overlay` takes chip's value; `--color-tab-unfocused-surface` takes surface's. `--color-scrim` and `--color-text-selection` keep their old values. `--color-live-bg`, `--color-good-bg` and `--color-bad-bg` stay at their tone with 12% opacity, as in the old file. | `popover` reads `--color-overlay` |

    The rows marked "(none)" are my picks, since the design system has no menu ground, scrim or selection colour.
    Jesse can change them.
  - **Type.** The registry components use Tailwind's size names (the `Button` has `text-sm` and `text-xs`), so those
    names take the design system's sizes, with their line heights:
    - `xs` is small, 12/16; `sm` is body, 13/19; `base` is heading, 14/20; `lg` is title, 15/20.
    - `xl` and larger are also title, since nothing in the app is larger.
    - Only five names are new: `meta` 11.5/16, `label` 11/16 (semibold, capitals, 0.06em tracking), `tree` 15/26,
      `code` 12/20 and `code-inline` 12/17.

    The old `--text-quiet`, `--text-body` and `--text-heading` become `xs`, `sm` and `base`. Headings and the title
    are semibold.
  - **Space.** The design system's steps (2 to 16, and 28 px) are Tailwind's 4 px unit: `0.5` to `4`, and `7`.
    Spacing maps by value, not by name. Tailwind's `--spacing` stays 4 px, and the design system's `space-N` names
    don't come in. Nor do the old `--space-1` to `--space-6`, because a class (`p-3.5`) is the one way to say 14 px.
    The old insets (`--rail-content-inset`, `--menu-row-inset` and the rest) come over with their names.
  - **Sizes.** They're named once in `@theme` (`--spacing-bar` 40, `--spacing-tab` 30, `--spacing-icon-button` 28,
    `--spacing-tree-row` 26, `--spacing-rail` 300, `--spacing-drawer` 280, `--container-chat` 700). That gives the
    classes `h-bar`, `h-tab`, `size-icon-button`, `w-rail` and `max-w-chat`. The old `--editor-tab-row-height` and
    `--center-head-row-height` become `bar`.

    The setting can't do the density alone. shadcn sets heights as classes inside each component (`h-9` in the
    Button). Changing `--spacing` would scale every padding with them: 28 px buttons need a unit of about 3.1 px,
    which turns `p-4` into 12.4 px. So a registry component's height class is edited on add to name a size
    (`h-9` becomes `size-icon-button`). That's a row in 9.1b. It meets the rule in `AGENTS.md`: the value lives once
    in `app.css`, and a component only names it.
  - **Shape and depth:**
    - The radii are `--radius-xs` 4, `-sm` 5, `-md` 6 and `-lg` 8. They replace shadcn's computed `--radius`
      (10 px). `xl` and larger are also 8, so a registry component's `rounded-xl` draws the design's largest corner
      rather than a square one.
    - `rounded-full` is only for the Board button and a tab's close disc. The old `--radius-pill` doesn't come.
    - No shadows: `--shadow-*: initial` and the old `--shadow-*` names don't come, and registry components lose their
      shadow classes on add (9.1b). The old `--focus-ring` stays, for controls with no edge of their own.
  - **Motion and icons:**
    - Motion is 120 to 180 ms fades and slides that end, under `motion-safe:`.
    - Icons are Lucide (`@lucide/svelte`): 16 px in the tab row and 14 px elsewhere, stroke 1.8. No emoji.
  - **Themes.** `app/src/lib/theme/themeRegistry.ts` is plain data. It has the old list as it was, all ten entries:
    - Assembly (the default), Houston, Dracula, Tokyo Night and Graphite;
    - the five ThemeMirror entries, Amy, Ayu Light, Cobalt, Rose Pine Dawn and Tomorrow, which keep the Assembly
      shell and change only the editor's syntax colours.

    The setting is one theme id from that list, never a light/dark toggle. `resolveThemeId` keeps its fallback: an
    id that isn't on the list, such as the `"dark"` base#7's pinned test saves, gives the default theme. Each theme's editor part arrives with W2, and its
    terminal palette with W4.
  - **One copy of the colours.** `app.css` holds Assembly's values, and nothing else repeats them.
    - Assembly and the five ThemeMirror entries carry no tokens, so applying them removes the values the last theme
      wrote.
    - Houston, Dracula, Tokyo Night and Graphite carry their old values. Each new role is set to the theme's nearest
      existing colour: `ink-body` and `ink-2` take `text`, `raised` and `chip` take `elevated`, `selected-strong`
      takes `selected`, and `line-soft` takes `border`. The diff grounds take the good and bad tints. `aqua` and
      `lavender` take the theme's own terminal cyan and magenta.
    - The old `scripts/themeRegistry.test.ts` existed only to catch two copies drifting apart, so it doesn't come.
  - **Applying a theme.** `themeService.ts` is ported to this app's rules:
    - The theme id is a setting saved through `save` (5.9), with no `localStorage`.
    - It writes the theme's variables on the document root only, since there's one set on `:root` and no shell
      element to repaint.
    - It has no listeners outside `lib/host/` and no timers.
    - The editor and terminal appliers arrive with W2 and W4. `clearTheme` isn't needed, because there's no other page
      to hand the document back to.
  - **Fonts.** These are system faces, and the person picks their own:
    - `fontRegistry.ts` has one preset for each, "System" (the system UI face, and the system monospace). The rest of
      the choices are the families installed on the machine, which `mergeFontChoices` adds from a host call in
      `lib/host/`.
    - `fontService.ts` writes `--font-sans` and `--font-mono` on the document root, saved through `save`.
    - The old `fonts.css` and its 13 bundled faces don't come. Inter goes too: the `@fontsource-variable/inter`
      import in `app.css` and the package from 91a01e6.
  - **Preferences:**
    - The spike's `'system' | 'light' | 'dark'` setting is replaced by the theme id and the two font choices.
    - The key stays `theme`, so base#7's pinned test, which saves `"dark"` as a value and reads it back, still holds
      unchanged.
    - There's no "follow the system", so Tauri's theme-changed event isn't needed.
  - **Contrast:** `contrast.ts` doesn't come. Nothing imports it at 2089290, and a contrast test over the themes
    would break `AGENTS.md`'s rule against tests on colour tokens or contrast.
  - **Rules that carry over:**
    - The drawer follows the tabs through explicit state (a value the shell sets), never CSS `:has()`.
    - No test asserts a CSS class, a colour or a pixel value.
    - The design system's component previews are design evidence, not code to paste. Markup is built from the
      registry components.
  - **Files:** `app/src/app.css`; `app/src/lib/theme/` (`themeRegistry.ts`, `themeService.ts`, `fontRegistry.ts`,
    `fontService.ts`); the host call for installed fonts in `app/src/lib/host/`; and the components
    under `app/src/lib/components/`, each edited on add where the guard needs it (9.1b).
  - **Proof:** a feature check in the window: Assembly and one other theme, a font change, and the choice surviving
    a quit.

### W. The workbench

- **W1. Projects and checkouts** (base#1, Sol P10).
- **W2. Files and the editor** (Sol P19, P20).
- **W3. Git and worktrees** (Sol P21).
- **W4. Terminals** (Sol P22).
  - Terminal output is a topic on the same socket, not a socket of its own.
- **W5. Language servers** (Sol P23).
  - There's one server per language per checkout, shared by every session and every editor tab (Jesse's comment). It
    starts on demand and stops when the last user closes.
  - **Test:** two sessions on one checkout share one server process; the test checks the pid.
- **W6. The browser** (Sol P24).

Each W phase:

- **Files:** one `Application/Features/<Feature>/` folder, Infrastructure only for a new outside system (a process,
  a new kind of connection), and its page folder.
- **Tests:** Sol's named test group for that step.
- **Proof:** check4, plus a feature check in the window.

### P. Product features

- **P1. Messages and machine routing** (Sol P25). It reads the old app's messaging as the reference, since Jesse says
  it worked. The polls in `RemoteOutbox.cs:44` and `MessageStatusTool.cs:30` become pushes.
- **P2. Workflows** (Sol P27).
- **P3. Build Board** (Sol P28). Jesse's design (plan thread, 2026-10-26 19:10: reui.io agent-activity 1, 2 and 5,
  "These are all pieces that i would like implemented, for different parts of the app"):
  - One card per task, in one of three states:
    - **Running** (agent-activity-2, Live Run Feed): a title, one live step line, and a footer with steps, files,
      tools and duration.
    - **Needs you** (agent-activity-1, Approval Run Console): a question answered on the card, or Approve/Deny.
      These cards sort first.
    - **Done or failed** (agent-activity-5, Run Outcome Report): done, failed and skipped counts, the files changed,
      and retry for the failed items.
  - At rest a card is at most 4 lines; a click opens the full feed.
  - Built by hand in Svelte from the shadcn.io Svelte components, since ReUI is React only and Pro.
  - The backend pushes each task's changes on one board topic, and durations come from backend timestamps. The
    demos' `setTimeout` replay isn't ported.
- **P4. Phone** (Sol P29). The phone speaks the same socket protocol, with paired credentials.
- **P5. Notion Tasks** (Sol P30).
- **P6. Usage and resources** (Sol P31).
- **P7. Run** (Sol P32).
- **P8. Providers and Settings** (Sol P33).

Each P phase has the same shape as the W phases.

### R. Release

- **R1. Remove what's left of the old paths** (Sol P34).
- **R2. Package and measure** (Sol P35, P36).

## 12. What this plan doesn't settle

- **The 30-file limit per folder** (section 4, step B2). It's Jesse's number to change: one constant in
  `ArchitectureTests`, with no exceptions.
- **Audit retention is 30 days** (5.6). That's a number Jesse can change: it and the 5,000-row cap per prune are constants in `AtomicWriter.cs`.
- **The 300-line file limit,** which waits for Jesse. If he picks 300, the 7 oversize files are split in phase C1.
- **`dockview-core` for F1**, pending Jesse's OK. Not added yet.
  - **Why not shadcn's Resizable (PaneForge):** it drags the dividers inside a fixed set of panes, but a pane can't be
    moved somewhere else, which Jesse asked for.
  - **What its source shows.** I read version 6.6.1, the one already on disk in a mac-command-bar checkout on
    agent-workbox (`dist/dockview-core.noStyle.js`), with no downloads:
    - It has 22 `setTimeout`, 17 `requestAnimationFrame` and 7 `queueMicrotask` calls.
    - During a drag it listens for `pointermove`, `pointerup` and `pointercancel` on the document or the window, and
      removes those listeners when the drag ends. At the end of a drag it runs a few zero-delay timers to clean up,
      and a 300 ms fallback timer in case an animation's end event doesn't come.
    - It moves a tab's underline every frame for 250 ms after a tab change, then stops.
    - One frame loop never stops: it reads the window's screen position every frame, but only for panes popped out
      into their own window. So the app doesn't use popout windows.
    - Its stylesheet, `dockview.css`, has no `infinite`, but it has 10 `:has(`: 8 in its "spaced" themes and 2 on
      the tab-group chip. So it isn't imported as is. The rules the app needs are written into `app/src/app.css`
      without `:has()`, and the build backstop (9.1) proves none got in.
- **Timers inside packages** (`bits-ui`'s tooltip delay, `Collapsible`'s tick after a toggle, `dockview-core`'s
  drag timers): Jesse's call whether to accept them. The at-rest checks (9.1) are the net either way.
- **Light-named themes** (F2). Ayu Light, Rose Pine Dawn and Tomorrow keep the Assembly shell and change only the
  editor, as in the old app. Making them light the whole window is a possible follow-up, not part of F2.
- **Note: my picks for colours the design system doesn't name** (F2): the menu ground takes chip's value, and the
  scrim and the selection colour keep the old values. Jesse can change them.
- **The packages for the checks** (prettier, knip), which wait for Jesse's OK. Tailwind and shadcn-svelte are in
  (91a01e6).
- **base#11** ("Threads made on a remote machine show in the Mac's list too") is Jesse's product call. It's added to
  `docs/behavior/base.md` in phase C2, as a draft line. That file isn't in `scripts/locked-paths.txt`, so it needs no
  OK.
- **Typing and then quitting right away** keeps the value, because each keystroke was already sent (5.9). That still
  has to be shown in the window on the Mac (step B6).
