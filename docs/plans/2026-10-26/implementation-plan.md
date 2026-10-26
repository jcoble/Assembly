# Assembly: implementation plan

Written 26 October 2026 by Claude Code on agent-workbox, at lane/base `9e51121`, for Jesse to review. It replaces
the plan in `docs/# Assembly .md` (Sol's), which stays in the repo unchanged as the long list of features to port.
This plan is the part that list was missing: the base every feature is built on, spelled out file by file, so that
each later feature has one obvious way to be written and nothing to invent.

Nothing in this plan is built yet. No code and no locked file changed to write it. The measurements in section 1
were run on agent-workbox in a scratch folder, from packages already in the local NuGet cache.

How to read it:

- **Section 1** has the four things only Jesse can decide. Each has the facts and the options. Nothing after section
  1 is started until he has answered them, and the plan says where an answer changes a step.
- **Section 2** says where each of Jesse's comments in Sol's doc went.
- **Sections 3 to 9** are the base: the rules, the solution layout, the shared plumbing, every timer in the page today
  and where it goes, the promise chains, and the guards and lint.
- **Section 10** is the base's commits, in order. Each has its files, the failing test written first, the proof, and a
  rough size.
- **Section 11** is the later phases. They're shorter, but each still has its files, tests and proof.
- **Section 12** lists what this plan doesn't settle.

## 1. Decisions for Jesse

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

Every other part of the plan is the same whichever he picks. Only `Assembly.Infrastructure/Persistence/` and the
`IAppDatabase` / `IApplicationDbContext` interface in section 5.6 change, and step B3 in section 10 is written for
both.

### D2. The lag meter, the counters sampler and the soak

These three measure the page. Today they use timers (section 7): the lag meter needs a callback on each frame the
page draws (`requestAnimationFrame`) to measure frame gaps and key-to-paint delay, and the soak waits between the
steps it drives. Zero timers in `app/src` means they can't stay where they are. The options:

- **A. Measure tools outside `app/src`.** Move them to `app/measure/` (TypeScript), which the guard doesn't scan. The
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

**This plan follows the article**, since that's the link Jesse gave: Domain, Application, Infrastructure, and two
hosts (the Mac's and the remote's). It borrows RentalCommand's idea of one write executor that every write goes
through. If he'd rather have the Core / Data / Api / Engine names, only the project names in section 4 change.

### D4. Bytes over the socket

One path means attachments go over the socket too. Today, `POST /threads/{id}/attachments` and
`GET .../attachments/{name}` are the only binary HTTP calls. The plan sends them as calls whose arguments or result
carry the bytes as base64, capped at 10 MB per image, the size the composer allows today. This adds about a third to
the size of an upload, which is small for screenshots. The other way is a binary socket frame with a small header,
which is more to write. The plan uses base64 unless Jesse wants binary frames.

## 2. Where Jesse's comments went

| His comment (in `docs/# Assembly .md` or his 26 October message) | Where it is in this plan |
| --- | --- |
| No old data to bring over | Section 3, rule 14: no migration of old data, the schema starts fresh. |
| Find out why MCP servers weren't a problem in the old app before changing them | Section 11, phase C3, starts with that investigation, and changes nothing until it has an answer. |
| One language server shared by every session, not one per session | Section 11, phase W5: one server per language per checkout, shared, and stopped when the last editor closes. |
| Machine-to-machine messaging works in the old app; the timers came out of it | Section 11, phase P1 reads the old messaging code as the reference. Its polling (`RemoteOutbox.cs:44`, every second) becomes a push on the machine socket. |
| A base implementation on the backend like the front end's, as a PR for review before merging | Section 10: the base is built as commits on one branch, with the preferences feature rebuilt as the example every feature copies. Jesse reviews the PR and merges it himself. |
| Shared plumbing for repeated work (atomic save, logging, auditing, socket send and receive) through middleware or DI, with features calling an interface | Sections 5.2 to 5.8: one implementation each, registered in DI, wrapped around every call by the dispatcher. |
| Clean Architecture after codewithmukesh, DbContext rather than repositories | Sections 4 and 5.6, and decisions D1 and D3. |
| Timers and ticks made the old app unusable; one allowlist entry grew to more than 50 | Sections 6 to 9: no timers in `app/src`, and no allowlist file. The guard has no list anyone can add to. |
| No allowlist at all; the debounce is the only timer he'd allow, unless there's another way | Section 5.9: the debounce moves to the backend, so the page has no timers at all and needs no exception. |
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
   Nothing else in `app/src` calls `fetch`, opens a `WebSocket`, or adds a `window` or `document` listener. Enforced
   by the guard, which names that one file inside the script.
3. **Tauri only in `app/src/lib/host/`.** Enforced by the guard, which names that folder inside the script.
4. **`async`/`await` with an `AbortSignal`, never `.then`, `.catch` or `.finally`.** Enforced by the guard.
5. **Every backend call has a cancellation token, and passes it to everything it awaits.** CA2016 is a build error
   (`dotnet/.editorconfig`). Closing the socket cancels every call still running on it.
6. **Every save is atomic.** A call that writes runs inside `IAtomicWriter.WriteAsync`: one connection, one
   transaction, an audit row, commit, and only then publish. A call can't write any other way, because only the
   writer hands out a write connection. Enforced by the type: `IAppDatabase` is read-only.
7. **Features register only through the dispatcher.** A feature adds its calls and topics in its
   `<Feature>Feature.cs`. No feature maps a route, reads a socket or opens a database connection itself. Enforced by
   `scripts/lint.d/40-one-dispatcher.sh`, which replaces `40-routes.sh`.
8. **Layers point inward.** Domain references nothing, Application references only Domain, Infrastructure references
   Application, and the hosts reference both. Enforced by the project references and by `ArchitectureTests`
   (step B2).
9. **One class per file, organized by feature inside each layer.** This is today's rule, kept.
10. **Animations end.** Short motion tied to an interaction is fine (120–250 ms); a loop at rest isn't. From
    mac-command-bar.
11. **No CSS `:has()`.** Enforced by the guard.
12. **TypeScript only.** No `.js`, `.mjs` or `.cjs` files. This is today's rule; `scripts/lint.d/00-typescript-only.sh`
    enforces it.
13. **Filtering, sorting, counting and paging happen in SQL.** From mac-command-bar.
14. **No old data comes over.** The schema starts fresh, and there's no migration from the old app. From Jesse's
    comment.
15. **KISS, YAGNI, and one way only.** A change is done when the old way is deleted. From mac-command-bar, kept word
    for word.
16. **mac-command-bar's sections that carry over unchanged:**
    - "What we don't trade away", with its file paths updated.
    - "Things that do real damage".
    - "Providers behave the same", with the provider folder becoming `Assembly.Infrastructure/Acp/Providers/`.
    - "Check every place the change reaches".
    - "Testing", with check4 replacing the four-scenario script.
    - "Review guidelines".
    - "Reaching Jesse".
17. **Dropped from mac-command-bar,** because they describe the Rust app:
    - the Rust folder map;
    - the 600-line baseline file (the size rule waits for Jesse's 300-line decision);
    - the `Architecture:` commit line and `main-architecture-explained.html`, which come back when that page is
      written for this repo.

## 4. The solution layout

The projects sit directly under `dotnet/`, so scripts that use `dotnet/Assembly.*` keep working. Central package
versions go in `dotnet/Directory.Packages.props`, as the article does.

```text
dotnet/
  Assembly.slnx
  Directory.Build.props          net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors (today's)
  Directory.Packages.props       new: one version per package
  .editorconfig                  today's, CA2016 an error
  Assembly.Domain/               references nothing
  Assembly.Application/          references Domain
  Assembly.Infrastructure/       references Application
  Assembly.Mac/                  the Mac's host: references Application, Infrastructure
  Assembly.Remote/               the remote host: references Application, Infrastructure
  Assembly.Tests/                one test project, folders by layer (below)
```

The tests stay in one project rather than one per layer. This keeps the locked paths
(`dotnet/Assembly.Tests/Pinned/**` and the READMEs in `scripts/locked-paths.txt`) where they are, so the move needs
no OK for them.

### What goes in each layer

| Layer | Holds | Doesn't hold |
| --- | --- | --- |
| Domain | The records and rules that are true whatever runs them: `ThreadInfo`, row kinds and payloads, the markdown block model, events, `Preference`, and invariants (a `DomainException` for a broken one). | I/O, JSON, SQL, sockets, time (time comes in as an argument). |
| Application | What a feature does: its calls and topics (`<Feature>Feature.cs`), the use cases behind them, and the interfaces it needs from outside (`IAppDatabase`/`IApplicationDbContext`, `IAtomicWriter`, `ITopics`, `IMachine`, `IAgentLauncher`, `IBackendClock`). Also the shared plumbing's contracts (`ICallRegistry`, `CallContext`, `CallFailed`). `DependencyInjection.cs` has `AddApplication()`, `AddMachineFeatures()` and `AddMacFeatures()`. | Any concrete SQL, socket, process or file code. |
| Infrastructure | The implementations: SQLite (`Persistence/`), the socket endpoint and dispatcher (`Socket/`), the atomic writer and audit (`Persistence/`), topics (`Topics/`), the backend clock, ACP and JSON-RPC, agent processes and git, the attachment file store, and (registered only by the Mac host) the remote machine link, SSH tunnel and follower. `DependencyInjection.cs` has `AddInfrastructure()` and `AddMacInfrastructure()`. | Feature rules. |
| Hosts | `Program.cs`: parse arguments, call the `Add*` methods, map the socket endpoint and `/mcp`, run. | Anything else. |

### Where today's folders go

Today's folders are listed in `dotnet/README.md`. Step B2 moves them as below, renaming only namespaces. Behavior
doesn't change in that step.

| Today | Goes to |
| --- | --- |
| `Assembly.Engine/Rows/` payload records, `RowKind` | `Assembly.Domain/Rows/` |
| `Assembly.Engine/Rows/RowConverter*` (ACP updates to rows) | `Assembly.Application/Rows/` |
| `Assembly.Engine/Markdown/` | `Assembly.Domain/Markdown/` (block model) and `Assembly.Infrastructure/Markdown/` (the Markdig parse) |
| `Assembly.Engine/Events/` | `Assembly.Domain/Events/` |
| `Assembly.Engine/Threads/ThreadInfo`, config records | `Assembly.Domain/Threads/` |
| `Assembly.Engine/Threads/ThreadStore` | `Assembly.Infrastructure/Persistence/Threads/` |
| `Assembly.Engine/Store/` (`RowStore`, `Schema`) | `Assembly.Infrastructure/Persistence/Rows/` |
| `Assembly.Engine/Sessions/` (`SessionHost`, `SessionRegistry`, `SessionOpener`, `Subagents`) | `Assembly.Application/Sessions/` |
| `Assembly.Engine/Sessions/SharedAdapter` | `Assembly.Infrastructure/Agents/` |
| `Assembly.Engine/Acp/` | `Assembly.Infrastructure/Acp/` |
| `Assembly.Engine/Agents/` | `Assembly.Infrastructure/Agents/` |
| `Assembly.Engine/Live/LiveHub` | `Assembly.Infrastructure/Topics/` (it becomes the `rows` topic, step C1) |
| `Assembly.Engine/Machines/IMachine`, `MachineSet` | `Assembly.Application/Machines/` |
| `Assembly.Engine/Machines/LocalMachine` | `Assembly.Infrastructure/Machines/` |
| `Assembly.Engine/Helpers/` tools | `Assembly.Application/Helpers/` (the rules) and `Assembly.Infrastructure/Mcp/` (`/mcp` over HTTP) |
| `Assembly.Engine/Attachments/` | `Assembly.Infrastructure/Attachments/` |
| `Assembly.Engine/Transcript/` | `Assembly.Infrastructure/Transcript/` |
| `Assembly.Engine/Api/` (HTTP routes, `LiveSocket`, `TokenGuard`) | `Assembly.Infrastructure/Http/`, kept only until each route moves onto the socket, then deleted (section 11) |
| `Assembly.Engine/Features/` (`FeatureCalls`, feature lists) | Deleted in step B7; replaced by `Socket/` and `Application/DependencyInjection.cs` |
| `Assembly.Mac/Remote/` | `Assembly.Infrastructure/Remote/`, registered only by `AddMacInfrastructure()` |
| `Assembly.Mac/Preferences/` | `Assembly.Domain/Preferences/`, `Assembly.Application/Preferences/`, `Assembly.Infrastructure/Persistence/Preferences/` (step B7) |
| `Assembly.Mac/Page/`, `Assembly.Mac/Debug/` | `Assembly.Application/Diagnostics/` and `Assembly.Infrastructure/Diagnostics/` (step B8) |
| `Assembly.Remote/Smoke`, `Record`, `Replay` | Stay in `Assembly.Remote/` (they're tools of that host) |

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
- 30 seconds per call (`CancelAfter` in the dispatcher). A call that runs over answers `timeout`. Saves aren't calls
  the page waits on, so the limit doesn't apply to them.

### 5.2 The dispatcher: one path for every call

`Application/Common/Calls/ICallRegistry.cs`:

```csharp
public interface ICallRegistry
{
    void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, CallContext, Task<TResult>> handler);
    void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
        Func<TArgs, IWriteScope, CallContext, Task<TResult>> handler);
    void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Func<IReadOnlyDictionary<string, TValue>, IWriteScope, CallContext, Task> flush);
    void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<string?, CallContext, Task<IReadOnlyList<TItem>>> state);
}
```

`CallContext` carries the call's `CancellationToken`, the connection id, the machine, and a logger scope.

`Infrastructure/Socket/SocketDispatcher.cs` runs the same steps around every call, so no feature can skip one:

1. Find the handler. If there isn't one, answer `not_found`.
2. Read `args` with the handler's `JsonTypeInfo`. If that fails, answer `bad_args`.
3. Start a logger scope (`feature/method`, the call id, the connection) and log the call at Debug.
4. Make the token, linked from the connection's token, the call's `cancel` frame, and the 30-second limit.
5. For `Write`, run the handler inside `IAtomicWriter.WriteAsync` (5.6). For `Read`, call it directly.
6. Answer `result`. A `CallFailed` answers `failed` with its message. A cancellation answers `cancelled`, or
   `timeout`. Anything else is logged at Error and answers `failed` with "feature/method failed". This keeps
   today's 500 handling (`FeatureCalls.cs`).

A feature's file is short. For example, `Application/Preferences/PreferencesFeature.cs`:

```csharp
internal sealed class PreferencesFeature(PreferenceReader reader) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Topic("preferences", AppJson.Default.Preference, (_, call) => reader.AllAsync(call.Cancellation));
        calls.Save("preferences", AppJson.Default.JsonElement, PreferenceWriter.FlushAsync);
    }
}
```

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

`Application/Common/Persistence/IAtomicWriter.cs`:

```csharp
public interface IAtomicWriter
{
    Task<T> WriteAsync<T>(string what, CallContext call, Func<IWriteScope, Task<T>> write);
}
public interface IWriteScope
{
    // D1 A or B: the context, already inside the transaction.
    // D1 C: Execute(sql, parameters) and Query(...) on the one open connection.
    void Publish(string topic, string? key, object change); // queued, sent after commit
}
```

`Infrastructure/Persistence/AtomicWriter.cs` handles every write the same way:

1. Take the database's one write lock.
2. Open the one connection and begin a transaction.
3. Run the feature's write.
4. Insert one audit row: `audit(at, connection, feature, method, what)`.
5. Commit, then send the queued publishes.
6. If the write fails, roll back and publish nothing.

No transaction is ever held open across an agent call, a network call, or a wait for a socket.

Reads go through `IAppDatabase`, which has no write methods. Under D1 A or B, it's `IApplicationDbContext` with
`AsNoTracking` queries.

### 5.7 Topics

`Infrastructure/Topics/Topics.cs` keeps each connection's subscriptions. When a page subscribes, it gets
`state(key)`, then each change the writer published for that topic and key. Unsubscribing, or closing the socket,
removes the subscription. The `rows` topic is today's `LiveHub` (phase C1).

### 5.8 The one backend clock

`Infrastructure/Clock/BackendClock.cs` is one `PeriodicTimer` on the injected `TimeProvider`, ticking once a second.
It runs only while something subscribes to the `clock` topic, and its `change` carries `{ now }`. The page's turn
clock (section 7, row 8) and, under D2 B, the counters sampler read it. Tests use `FakeTimeProvider`, as the spike
does now.

The backend's other waits are listed in the timer inventory: the `SessionHost` coalesce timer, the adapter idle
grace, the SSH backoff, and the follower's backoff. They stay where they are, since they're backend work that needs
time and isn't a page clock. Phase P1 replaces two polls with pushes: `RemoteOutbox.cs:44`, every second, and
`MessageStatusTool.cs:30`, every 250 ms.

### 5.9 Saves: the page sends each change, the backend batches

The page sends every change, with no debounce: `save(feature, key, value)` sends `call` with `feature/set`. The
backend's `Infrastructure/Persistence/SaveBatcher.cs` keeps the newest value per key. 300 ms after the last change,
on the backend clock, it runs the feature's `flush` inside `IAtomicWriter.WriteAsync`: one transaction for all the
waiting keys, then a publish on the feature's topic, so other windows see the change.

- A read always includes waiting values, so a load can never bring back an older value, and the page doesn't need
  `unsavedKey`.
- Each change reaches the backend as it happens, so closing the window loses nothing, and the `pagehide` and
  `visibilitychange` listeners are deleted.
- When the backend shuts down, it writes whatever is waiting before it exits.
- base#7 ("Settings save a moment after a change, not on every keystroke") still holds: the database write happens
  once, 300 ms after the last change.

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
| 8 | `lib/Conversation.svelte:245`: `delay(1000)` in a loop | The running turn's elapsed time | **The backend clock.** The page subscribes to `clock` while a turn runs, and shows `now - startedAt`. |
| 9 | `lib/call.ts:52,84,99`: `AbortSignal.timeout(30 s)` | The call timeout | **The backend.** The dispatcher's 30-second limit answers `timeout` (5.1). `call.ts` is deleted in step B7. |
| 10 | `lib/call.ts:73`: `debounce(saveNow, 300)` | The settings debounce | **The backend.** `SaveBatcher` (5.9). |
| 11 | `lib/call.ts:102,105`: `pagehide` and `visibilitychange` | Flushing saves on close | **Deleted.** Each change already reached the backend (5.9). |
| 12 | `lib/composer/Composer.svelte:81`: `queueMicrotask` | Focus and selection after a slash-command pick | **Deleted.** Use `flushSync`, then focus, as `Composer.svelte` already does after a draft change. |
| 13 | `debug.svelte.ts:44,70,94` (signal and socket listeners), `TrackedWebSocket` | Counting sockets for the soak | `TrackedWebSocket` is folded into `socket.ts` (one socket, counted there). The rest is deleted with row 1. |

The single named exception: **none.** The debounce is handled by row 10, so nothing in the page needs one. If Jesse
would rather keep a page-side debounce, it would be the only one, written inside `socket.ts`, and the guard would name
that one line's function, not a file list.

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
- `HOST=app/src/lib/host/`, the only folder that may use Tauri.

Every file in `app/src`, those included, fails on any of these:

- `setTimeout`, `setInterval`, `requestAnimationFrame`, `queueMicrotask`
- Svelte's `tick(`, `AbortSignal.timeout`
- `fetch(`
- `window.`, `document.`, `globalThis.` or `self.` followed by `addEventListener(`
- `.then(`, `.catch(`, `.finally(`
- a `:has(` in CSS
- `observe(document.body`
- a markdown parser import, except in `lib/editor/preview/`, as today

These are checked in every file:

- A `WebSocket` outside `TRANSPORT`.
- `@tauri-apps/` or `listen(` outside `HOST`.

Its self-test lines, which are today's design, stay. A new probe is added for each new rule.

Changing these rules means changing the script, which is locked. So adding a rule or a place takes an OK in
`.pinned-approvals`, and nobody can add their own file to a list, because there's no list.

`guards.sh` and `guards-allowed.txt` are locked on main now (`scripts/locked-paths.txt`), since PR #1 merged.
**Step B10 needs Jesse's OK line** in `.pinned-approvals` for both files, because the lane that wants the change can't
OK it (`AGENTS.md`, "Who may OK").

### 9.2 `scripts/lint.d/`

- `00-typescript-only.sh` stays as it is.
- `40-routes.sh` guards today's HTTP `Map()` pattern, which goes away. It's replaced by
  `40-one-dispatcher.sh`, which fails on any of these under `dotnet/`, outside `Assembly.Tests`:
  - `MapGet(`, `MapPost(`, `MapPut(`, `MapDelete(`, `.Map(` and `MapGroup(`, except in
    `Assembly.Infrastructure/Socket/SocketEndpoint.cs` (the socket) and
    `Assembly.Infrastructure/Mcp/McpEndpoint.cs` (`/mcp`), which are named in the script;
  - `new SqliteConnection(` (or `new ApplicationDbContext(`), except in `Assembly.Infrastructure/Persistence/`;
  - `AcceptWebSocketAsync(`, except in `SocketEndpoint.cs`;
  - `BeginTransaction(`, except in `AtomicWriter.cs`.
- While the chat routes still exist (until phase C1 ends), `40-one-dispatcher.sh` lists them as a legacy list that
  can only shrink, as `40-routes.sh` does today. The list is deleted when it's empty.
- New checks, all run by `scripts/lint.sh`:
  - `50-dotnet-format.sh`: `dotnet format --verify-no-changes`.
  - `60-svelte-check.sh`: `svelte-check`, with zero errors.
  - `70-architecture.sh`: runs `ArchitectureTests`.
  - Once Jesse decides the size limit, a size check.
- **When the lint configs get locked:** after step B2 (the move) and B11 (the new checks) are on main, step B12 adds
  `scripts/lint.d/**`, `dotnet/.editorconfig`, `dotnet/Directory.Build.props` and `dotnet/Directory.Packages.props`
  to `scripts/locked-paths.txt`. Before that, the move changes those files at every step, and locking them earlier
  would need an OK each time.

## 10. The base, commit by commit

All of this happens on one branch, `lane/base`, with each step a commit. Each code commit:

- has its failing test written first, and seen failing, before the code;
- runs the full `scripts/check4.sh` on the pushed commit, with the evidence committed alongside;
- is reviewed by the move thread before the next step starts.

The PR opens when B12 is done, and Jesse merges it.

### B1. Merge the AGENTS.md files

- **Files:** `AGENTS.md`.
- **Test:** none (documentation).
- **Proof:** the move thread and Jesse read the diff.
- **Size:** about 250 lines changed.

### B2. Move to the Clean Architecture layout (no behavior change)

- **Files:**
  - New: `dotnet/Assembly.Domain/Assembly.Domain.csproj`, `dotnet/Assembly.Application/Assembly.Application.csproj`
    with `DependencyInjection.cs`, `dotnet/Assembly.Infrastructure/Assembly.Infrastructure.csproj` with
    `DependencyInjection.cs`, `dotnet/Directory.Packages.props`.
  - Moved: every file in the "Where today's folders go" table in section 4.
  - Deleted: `dotnet/Assembly.Engine/`.
  - Changed: `MacBackend.cs` and `ServeRun.cs` call the `Add*` methods, plus the scripts that name
    `Assembly.Engine` (`dotnet/scripts/*.sh`, `shell/assembly.sh`, `scripts/check4.sh`).
- **Failing test first:** `Assembly.Tests/Architecture/ArchitectureTests.cs`. It loads each assembly and checks its
  references:
  - `Assembly.Domain` references no `Assembly.*`;
  - `Assembly.Application` references only `Assembly.Domain`;
  - `Assembly.Infrastructure` doesn't reference `Assembly.Mac` or `Assembly.Remote`.

  It fails today because the projects don't exist.
- **Proof:**
  - the test count is the same as before (201 passed and 4 skipped of 205, at `7805f12`), plus the new test;
  - check4 passes on all four cases;
  - `shell/assembly.sh build` publishes AOT;
  - the move thread sees the app run in the window on the Mac.
- **Size:** mechanical (namespaces and `using` lines across about 150 files), plus about 150 lines of new project
  files and DI.

### B3. One write path: `IAtomicWriter` and the audit

- **Files:**
  - Application: `Common/Persistence/IAtomicWriter.cs`, `IWriteScope.cs`, and `IAppDatabase.cs` (D1 C) or
    `IApplicationDbContext.cs` (D1 A or B).
  - Infrastructure: `Persistence/AtomicWriter.cs`, `Persistence/AppDatabase.cs` (C) or `ApplicationDbContext.cs`
    (A or B), `Persistence/Audit/AuditSchema.cs`, and `DependencyInjection.cs`.
- **Failing tests first,** in `Assembly.Tests/Infrastructure/Persistence/AtomicWriterTests.cs`, on a real temporary
  SQLite file:
  1. A write whose second statement throws leaves neither row, and publishes nothing.
  2. A successful write leaves its rows, one audit row, and its publish, which is sent after commit (the test
     subscribes and checks the order).
  3. A failing commit (an injected failure) leaves nothing and publishes nothing.
  4. Two writes at once run one after the other.
- **Proof:** the tests, run, with their counts. check4 passes.
- **Size:** 200 to 300 lines with tests.

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
  4. Subscribing again after a reconnect gives the current state, not a replay.

  On the page, a `subscribe` test applies `state` and `change` to a `$state` map.
- **Proof:** the tests, and check4.
- **Size:** 300 to 400 lines with tests.

### B6. Saves batched on the backend

- **Files:**
  - Application: `Common/Calls/ICallRegistry.Save`.
  - Infrastructure: `Persistence/SaveBatcher.cs`.
  - Page: `socket.ts` gains `save`.
- **Failing tests first:**
  1. Three changes in 300 ms make one write with the newest value of each key.
  2. A read while a change is waiting returns the waiting value.
  3. Shutting down writes what's waiting.
  4. A failed write keeps the values waiting and reports once.

  All of these use `FakeTimeProvider`. On the page: `save` sends each change, with no timer.
- **Proof:** the tests, and check4.
- **Size:** 200 to 300 lines with tests.

### B7. Preferences rebuilt on the base (the example every feature copies)

- **Files:**
  - Domain: `Preferences/Preference.cs`.
  - Application: `Preferences/PreferencesFeature.cs`, `PreferenceReader.cs`, `PreferenceWriter.cs`,
    `PreferencesJson.cs`, `README.md` (moved from `Assembly.Mac/Preferences/README.md`, which is locked, so it needs
    the move thread's OK for a moved file).
  - Infrastructure: `Persistence/Preferences/PreferencesSchema.cs`.
  - Page: `app/src/lib/preferences/preferences.svelte.ts` (subscribe and save), and its `README.md`.
- **Deleted:** `app/src/lib/call.ts`, `dotnet/Assembly.Engine/Features/FeatureCalls.cs`, `PreferencesRoutes.cs`,
  `PreferenceStore.cs`, and the `/preferences/*` HTTP route.
- **Failing tests first:**
  1. base#7: a change is written once, 300 ms after the last one.
  2. Two windows: a change in one reaches the other's subscription.
  3. A load can't put back an older value, even if the subscription arrives while a change is waiting.

  The page tests change with it. `app/test/draft/base/call.test.ts` becomes `socket.test.ts`, and
  `preferences.test.ts` is rewritten for the subscription.
- **Proof:**
  - check4 passes, with `scripts/features/preferences.sh` rewritten as `preferences.ts` over the socket;
  - the move thread sees, in the window on the Mac, a theme change that survives a quit, and a second window that
    follows it.
- **Size:** 300 to 450 lines, offset by about 400 deleted.

### B8. The Mac's page calls onto the socket

- **Files:** Application `Diagnostics/DiagnosticsFeature.cs` (memory sample, App Nap begin and end, dropping the link
  for the debug control), and Infrastructure `Diagnostics/*`.
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

**Size:** 300 to 500 lines changed, mostly deleted.

### B10. The guard with no allowlist

- **Files:** `app/scripts/guards.sh`, and deleting `app/scripts/guards-allowed.txt`. Both are locked, so this
  **needs Jesse's OK line.**
- **Test first:** new self-test probes for each new rule (`.then(`, `queueMicrotask`, `AbortSignal.timeout`, `fetch`
  in `socket.ts` itself, a `WebSocket` outside `socket.ts`). Each fails against today's script.
- **Proof:** `guards.sh` passes on the tree after B9, and fails on each probe.
- **Size:** about 120 lines.

### B11. The lint checks

- **Files:**
  - `scripts/lint.d/40-one-dispatcher.sh`, replacing `40-routes.sh`;
  - `50-dotnet-format.sh`, `60-svelte-check.sh`, `70-architecture.sh`.
- **Test first:** each script gets a probe file it must fail on, run from a scratch copy, as for the guard.
- **Proof:** `scripts/lint.sh` passes on the tree, and each probe fails.
- **Size:** about 200 lines.

### B12. Lock the configs, then open the PR

- **Files:** `scripts/locked-paths.txt` adds `scripts/lint.d/**`, `dotnet/.editorconfig`,
  `dotnet/Directory.Build.props` and `dotnet/Directory.Packages.props`. That file is itself locked, so this needs an
  OK line.
- **Proof:** `scripts/check-pinned.sh` refuses a test edit to each newly locked file.
- **Then:** open the PR from `lane/base`, drafted until every step's proof is in, and send it to Jesse to review and
  merge.

## 11. The later phases

Each phase follows the base's pattern: a `<Feature>Feature.cs` in Application, its persistence in Infrastructure,
its store in `app/src/lib/<feature>/`, a check4 scenario, and the old route deleted in the same phase. Each starts by
drafting its behavior list (`docs/behavior/<feature>.md`) for Jesse to correct. Sol's step numbers are given so his
catalogue stays the reference.

### C. Chat on the socket

- **C1. Threads, rows, sends and config** (Sol P03, P07, P12, P14).
  - **Files:**
    - Application `Threads/ThreadsFeature.cs`, with its topic: one summary per thread (state, active run, pending
      ask, needs attention, background work, title, machine, online, project, last activity, unread);
    - `Rows/RowsFeature.cs`, the `rows` topic with `after`;
    - `Sessions/SendFeature.cs` (send, stop, answer, stop task), and `Config/ConfigFeature.cs` with a `config`
      topic;
    - the page's `threads/`, `rows/` and `composer/` stores.
  - **Deleted:** `Infrastructure/Http/*` chat routes, `LiveSocket.cs`, `app/src/lib/api.ts`,
    `app/src/lib/rows/client.ts`.
  - **Tests:**
    - base#8: a thread made while the page is subscribed arrives as an upsert;
    - base#9: the pending run is shown during the gap before the copied row arrives (the failing test is written
      before the fix), and the menus come from a host started with an empty home;
    - the rows-topic catch-up and `prevSeq` tests that exist today, moved;
    - attachments as base64 calls (D4).
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
  - **Files:** `Application/Sessions/*` and `Helpers/*`, on the base.
  - **Tests:** Sol's P08 and P16 test groups.
  - **Proof:** check4, plus the helper scenario.

### F. Frame and Look (the visible shell)

- **F1. The frame** (Sol P17, P18): sessions, chat, the right drawer, the bottom dock, and divider positions (base#4,
  base#5).
  - **Files:** `app/src/lib/shell/*`, and divider and tab state through `save` (5.9).
  - **Tests:** only the drawer and tab rules, as logic.
  - **Proof:** a feature check in the window, with screenshots.
- **F2. The look** (base#6): the design tokens and theme switching. This needs Jesse's OK on the packages
  (tailwindcss, bits-ui, lucide and the rest).
  - **Files:** `app/src/lib/styles/tokens.css`, and the components.
  - **Proof:** a feature check, light and dark.

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

- **Files:** an Application feature, its Infrastructure (processes, files), and its page store.
- **Tests:** Sol's named test group for that step.
- **Proof:** check4, plus a feature check in the window.

### P. Product features

- **P1. Messages and machine routing** (Sol P25). It reads the old app's messaging as the reference, since Jesse says
  it worked. The polls in `RemoteOutbox.cs:44` and `MessageStatusTool.cs:30` become pushes.
- **P2. Workflows** (Sol P27).
- **P3. Build Board** (Sol P28).
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

- **D1 to D4** (section 1).
- **The 300-line file limit,** which waits for Jesse. If he picks 300, the 7 oversize files are split in phase C1.
- **The packages for F2 and the checks** (prettier, knip), which wait for Jesse's OK.
- **base#11** ("Threads made on a remote machine show in the Mac's list too") is Jesse's product call. Adding the
  line changes `docs/behavior/base.md`, which is locked now, so it needs an OK.
- **WKWebView's behavior when the app quits** isn't a risk any more under 5.9, because each change is already on the
  backend. A quit while a batch is waiting is covered by the backend writing on shutdown, and that still needs to be
  shown working in the window on the Mac.
