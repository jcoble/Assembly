Recon, read-only, 2026-10-27, not re-verified unless marked

# Spec: the `machines` topic (plan C1, "Next after 1c")

Worktree: /home/blackcolours/dev/work/worktrees/Assembly/base-machines (HEAD 04752ce). All paths below are under `dotnet/`.
Line numbers were checked against this tree ("verified" = I read the line; "assumed" = inferred, not run). Nothing was built or run.

## 0. Corrections to the note (read first)

1. **`/version` has no `version` field.** It returns `{ formatVersion, commit }` (`Assembly.Infrastructure/Http/InfoRoutes.cs:63-70`, verified). The item's `version` must be `commit` (can be null: `InfoRoutes.cs:73-74`). Nothing else reads a version.
2. **`SessionSummaries` does not exist** in this tree or on `main` (`git grep` empty; only the plan mentions it, `implementation-plan.md:1488`). The pattern to copy is `SessionsFeature` + `SessionStore.Publish` (`SessionStore.cs:313-318`) for the topic, and `Topics.Publish` (`Infrastructure/Topics/Topics.cs:16-22`) for the lock. `MachineStatus` is new and small (below).
3. **Two classes called `CallFailed`.** `Assembly.Application.Common.Calls.CallFailed` (the one the socket dispatcher catches, `SocketDispatcher.cs:226`) and `Assembly.Infrastructure.Http.CallFailed` (old HTTP, `Infrastructure/Http/CallFailed.cs:4`, used by `FeatureCalls.cs:59`, `PreferencesRoutes.cs:42`). `RemoteMachine` must throw the Application one (`using Assembly.Application.Common.Calls;`). Code in namespace `Assembly.Infrastructure.Http` (the 9 route files) binds the bare name `CallFailed` to the WRONG one; write it fully qualified there (below). Verified.
4. **The tunnel cannot be "wired to the status in `DesktopServe.cs:23`".** The status needs the `Topics` instance, and that is created inside `DesktopBackend.StartAsync` (`DesktopBackend.cs:62`, verified). So `DesktopServe` passes the tunnel into `StartAsync` and the backend subscribes to a new `RemoteTunnel.Exited` event.
5. **The note's callers list for `MachineUnreachable` is incomplete.** Section 3 lists all 21 sites. 9 HTTP route sites would turn a remote's 404/500 from 502 into an unhandled 500 once `ReadJsonAsync` throws `CallFailed`. They need a one-line edit each.
6. **Flap bug in the note's rules.** `CheckFormatAsync` mismatch (`RemoteMachine.cs:184-186`): `SendRequestAsync` already recorded "answered = reachable" for the `/version` response, then `:186` records unreachable. `_formatChecked` stays false (`:187`), so EVERY call re-fetches `/version` and publishes true then false: two upserts per call. Fix (1 field, `_incompatible`): see 4.4.
7. **Startup state is unspecified in the note.** `DesktopServe.cs:23` waits only for the local forward port (`RemoteTunnel.cs:74`), not for the remote backend. Decision made here: a remote starts `reachable: false, version: null` ("not answered yet"), and the first tick after any `machines` subscribe probes at once, then every 15th tick. This deviates from the note's "every 15th tick" (a window opening would otherwise show red for 15 s). If you prefer `reachable: true` at start, delete `ResetProbe()` and flip the initial value; the tests below that rely on it are marked (S).
8. **`host` is redundant today.** `RemoteConfig.Name` IS the ssh host: `RemoteTunnel.cs:76` builds `new RemoteConfig(host, ...)`. Spec keeps `host` per the note, filled from the name. (YAGNI option: drop `host` until a name differs from the target; your call, it is 3 lines.)
9. **HTTP `GET /machines` stays** (`InfoRoutes.cs:24,76`, used by `RemoteMachineTests.cs:43`). The note's "machines/list is deleted" is the socket call only (`MachinesFeature.cs:20-21`, `SessionsJson.cs:21-22`). HTTP goes at "the cut".

## 1. Files and edit points

Paths in `Assembly.Application` = A, `Assembly.Infrastructure` = I, `Assembly.Desktop` = D, `Assembly.Tests` = T.

### 1.1 NEW `A/Common/Machines/MachineStatus.cs` (~60 lines)
Folder has 3 files, limit 30 (`ArchitectureTests.cs:19`). Namespace `Assembly.Application.Common.Machines` (namespace must match folder, rule at `ArchitectureTests.cs:86`).

```csharp
public sealed record MachineItem(string? Name, string Kind, string? Host, bool Reachable, string? Version);

public sealed class MachineStatus
{
    public MachineStatus(ITopics topics, IEnumerable<string> remotes);   // local first, then each remote reachable:false
    public IReadOnlyList<MachineItem> Items { get; }                      // lock-free read of the volatile snapshot
    public void Reachable(string machine, string? version = null);       // answered; version null = keep the old one
    public void Unreachable(string machine);
}
```
- Private `Lock _gate`, `volatile ImmutableArray<MachineItem> _items` (or `MachineItem[]`).
- `Set(name, update)`: `lock (_gate) { build the new item; if (equal to old) return; swap; _topics.Publish(MachinesFeature.Topic, key, JsonSerializer.Serialize(item, SessionsJson.Default.MachineItem)); }`. Publish inside the gate = lock order status then Topics (as the note says; same order as the writer-then-Topics rule in `Topics.cs:7-8`). The state function runs under the Topics lock and only reads `Items`, no `_gate` (so no inversion). Duplicate upsert for a subscribe racing a swap is harmless (assumed).
- Key = `item.Name ?? "local"`. Unknown machine names are ignored (do not add rows).
- Records `MachineItem` here; add `[JsonSerializable(typeof(MachineItem))]` to `A/Features/Chat/Sessions/SessionsJson.cs` (context already camelCase, `SessionsJson.cs:6`). `MachineSet.cs:1` already imports the Sessions namespace from Common, so this crossing already exists.
- `kind` strings: `"local"`, `"remote"`.

### 1.2 `A/Features/Chat/Sessions/MachinesFeature.cs`
- Add `public const string Topic = "machines";` next to `Feature` (:12). Use `Feature` for calls as today; keep both or set `Feature = Topic` (same string).
- Ctor :16 becomes `MachinesFeature(MachineSet machines, MachineStatus status, Action startClock)`; fields at :14.
- `Register` :18-28: delete the `list` call (:20-21). Add, first:
  `calls.Topic(Topic, SessionsJson.Default.MachineItem, item => item.Name ?? "local", (key, _) => { if (key is not null) throw new CallFailed("machines has no keys; subscribe without one"); startClock(); ResetProbe(); return _status.Items; });`
  (pattern: `BackendClock.cs:31-38` for the key guard and `Start()` call inside state; `SessionsFeature.cs:31-34` for the item/key lambda.) `calls.Topic` signature: `ICallRegistry.cs:36`.
- Keep `agents` (:22-27) unchanged.
- Add `public void Tick()`: called by the clock each second; MUST NOT throw. `if (Interlocked.Increment(ref _ticks) < Every) return; Interlocked.Exchange(ref _ticks, 0);` then per remote not already probing (`ConcurrentDictionary<string, bool>` or a `HashSet` under a lock), `_ = Task.Run(async () => { try { await machine.ProbeAsync(CancellationToken.None); } catch (Exception e) when (e is MachineUnreachable or CallFailed) { } finally { clear flag } })`. `const int Every = 15`. `ResetProbe()` sets `_ticks = Every - 1` so the next tick probes (S).
- Delete records `ListArgs`, `MachineList` (:30-32) and their entries `SessionsJson.cs:21-22`.

### 1.3 `A/Common/Machines/IMachine.cs`
After `:19` (`AgentsAsync`) add a default member so `LocalMachine` (`I/Machines/LocalMachine.cs`) and `FakeMachine` need no edit:
`/// <summary>Asks the machine whether it is up; a remote records the answer. This machine is always up.</summary>`
`Task ProbeAsync(CancellationToken cancellationToken) => Task.CompletedTask;`
This is the one interface addition; it is required because `MachineSet.Remotes` is `IReadOnlyList<IMachine>` (`MachineSet.cs:9`) and `MachinesFeature` is in Application (cannot name `RemoteMachine`).

### 1.4 `I/Remote/RemoteMachine.cs`
- usings (:1-13): add `using Assembly.Application.Common.Calls;`.
- Field after :31: `private readonly MachineStatus _status; private bool _incompatible;`
- Ctor :34: add `MachineStatus status` before `Action<string>? turnEnded = null` (optional param must stay last). Only caller is `DesktopBackend.cs:76` (grep verified, no tests construct it).
- :42 follower ctor: pass one more argument, `linked => { if (linked) Answered(); else _status.Unreachable(_name); }`.
- `CheckFormatAsync` :180-188: on the mismatch (:185-186): `_incompatible = true; _status.Unreachable(_name);` then the throw. On a match: `_incompatible = false; _formatChecked = true;` (:187). The version read is `/version` JSON at :184: fine as is.
- `SendRequestAsync` :213-224: change `return await _http.SendAsync(...)` (:218) to `var response = await _http.SendAsync(...); Answered(); return response;`. In the catch (:220-223) add `_status.Unreachable(_name);` before the throw at :222. Careful: `request` is `using` in the try, unchanged.
- New private `void Answered() { if (!_incompatible) _status.Reachable(_name); }` (fixes flap, correction 6).
- `ReadJsonAsync` :229-230: `throw new CallFailed($"{_name} answered {(int)response.StatusCode}: {body}");` (was `MachineUnreachable`). The message text is the same as today's, so the 502 bodies the old HTTP tests read are unchanged.
- New `public async Task ProbeAsync(CancellationToken cancellationToken)`: `var version = await JsonAsync(HttpMethod.Get, "/version", null, cancellationToken); if (version.GetProperty("formatVersion").GetInt32() != RowStore.FormatVersion) { _incompatible = true; _status.Unreachable(_name); throw new MachineUnreachable(...same message as :186) } _incompatible = false; _status.Reachable(_name, Text(version, "commit"));` (Share the mismatch check with `CheckFormatAsync` by having `CheckFormatAsync` call a private `async Task CheckVersionAsync(JsonElement)`; do not duplicate the message.) The probe uses the same 30 s `HttpClient.Timeout` (:40), the "usual timeout" the note means.
- `RefreshAsync` catch :201: `catch (Exception error) when (error is MachineUnreachable or CallFailed)`.

### 1.5 `I/Remote/RemoteFollower.cs`
- Primary ctor :18-20: add `Action<bool> linked` after `subagentCopied`.
- :52 `catch (MachineUnreachable error)` -> `catch (Exception error) when (error is MachineUnreachable or CallFailed)` (needs `using Assembly.Application.Common.Calls;`; this file imports `Assembly.Infrastructure.Machines`, not `.Http`, so no clash; verify at compile).
- :57-62 link-lost catch: first line of the block call `linked(false);` (before the log at :60).
- :86 (`await socket.ConnectAsync(...)`): next line `linked(true);` ("connected once ConnectAsync returns").

### 1.6 `I/Remote/RemoteTunnel.cs`
- Add after :40: `public event Action? Exited;`
- :146 (the "ssh exited" log): add `Exited?.Invoke();` on the line before the log (wrap nothing; handler is `status.Unreachable`, which does not throw).

### 1.7 `I/Clock/BackendClock.cs`
- Add `internal event Action? Ticked;` near :21.
- `Start()` :41 -> `internal void Start()` (body unchanged).
- Tick loop :62: `var clockWatched = _topics.HasSubscribers(Topic); var watched = clockWatched || _topics.HasSubscribers(MachinesFeature.Topic);` (`using Assembly.Application.Features.Chat.Sessions;`; the I project may name the A namespace, and `No_feature_names_another` at `ArchitectureTests.cs:97-116` only forbids feature-to-feature; `Clock` is under Infrastructure not a feature folder). `HasSubscribers(string)` is on the concrete `Topics` (`Topics.cs:30`), NOT on `ITopics` (`ITopics.cs:16` has only the keyed one): that is why this check lives in the clock, not in the feature.
- :71 publish only `if (clockWatched)`; then `Ticked?.Invoke();` wrapped `try/catch (Exception) { }` so a handler cannot kill the loop (:56-76 has only an OperationCanceled catch).
- Update the class summary (:7-11) and the stop condition text: runs while `clock` or `machines` has a subscriber.
- No new timer: still one `PeriodicTimer` (:55).

### 1.8 `D/Serve/DesktopBackend.cs`
- `StartAsync` :19 signature: add `RemoteTunnel? tunnel = null` after `RemoteConfig? remote = null` (5 existing callers unchanged, optional).
- After `var topics = new Topics();` (:62): `var status = new MachineStatus(topics, remote is null ? [] : [remote.Name]);`
- :76-77: pass `status` to `new RemoteMachine(..., log, status, sessions.Helpers.Row.TurnEnded)`.
- After `machines` is built (:78-80): `if (tunnel is not null) tunnel.Exited += () => status.Unreachable(remote!.Name);`
- :91-92 feature array: hoist `var clock = new BackendClock(topics, TimeProvider.System); var machinesFeature = new MachinesFeature(machines, status, clock.Start); clock.Ticked += machinesFeature.Tick;` above `ApiServer.Build` (:90) and use them in the array in place of `new BackendClock(...)` (:91) and `new MachinesFeature(machines)` (:92). The existing comment at :88-89 ("The clock isn't disposed") still holds.
- No dispose change needed (tunnel outlives backend: `DesktopServe.cs:24` `await using` declared first, disposed last; a late `Exited` only publishes to an empty Topics). Assumed fine.

### 1.9 `D/Serve/DesktopServe.cs`
- :25 pass `tunnel`: `DesktopBackend.StartAsync(options.Home, token, options.Port, launcher, Console.Out, remote, tunnel)`. (:23 itself unchanged.) 1 line.

### 1.10 Callers of `ReadJsonAsync` throwing `CallFailed`: see section 3.

## 2. Registration pattern (exact names)

- `ICallRegistry.Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem,string> key, Func<string?, CallContext, IReadOnlyList<TItem>> state)` (`A/Common/Calls/ICallRegistry.cs:36`). Impl `SocketDispatcher.Topic` registers `TopicFrames(NeedsKey:false, ...)` (`SocketDispatcher.cs:91-94`), so a keyless `sub` is allowed and a keyed one reaches `state(frame.Key, ...)`.
- `clock`: `BackendClock.Register(ICallRegistry calls) => calls.Topic(Topic, ClockJson.Default.ClockTime, _ => Second, (key, _) => { guard key; Start(); return [Now()]; })` (`BackendClock.cs:31-38`).
- `sessions`: `calls.Topic(Topic, SessionsJson.Default.SessionItem, session => session.SessionId, (key, call) => ...)` (`SessionsFeature.cs:31-34`); upserts come from the store: `scope.Publish(SessionsFeature.Topic, sessionId, SessionItem.From(session), SessionsJson.Default.SessionItem, keyedOnly: ...)` (`SessionStore.cs:317`) via `IWriteScope.Publish` (`IWriteScope.cs:18-21`).
- `machines` publishes OUTSIDE a write transaction, so it uses `ITopics.Publish(string topic, string? key, string json, bool keyedOnly = false)` (`A/Common/Topics/ITopics.cs:13`) with `JsonSerializer.Serialize(item, SessionsJson.Default.MachineItem)`, as `BackendClock.cs:71` does with the concrete `Topics`.
- Change frame on the wire (from `ClockSocketTests.cs:~108-110` `Change(...)`): `{"t":"change","sub":N,"op":"upsert","key":"<key>","item":{...}}`; state frame `{"t":"state","sub":N,"items":[{"key":"<key>","item":{...}},...]}` (`ClockSocketTests.cs:70`). Expect camelCase item: `{"name":"box","kind":"remote","host":"box","reachable":true,"version":"abc"}`; for local `name` is null (assumed: `SessionsJson` default writes nulls; check one frame in the first red test and fix the literal if `name` is omitted).

## 3. Every `MachineUnreachable` site (grep, verified) and what a non-2xx `CallFailed` does to it

Throw sites: `MachineSet.cs:12` (unknown name, stays `MachineUnreachable`, never recorded), `RemoteMachine.cs:186,222` (stay, now record), `RemoteMachine.cs:230` (becomes `CallFailed`), `FakeMachine.cs:90` (T, unchanged).

| Catch site | Today | After non-2xx = `CallFailed` | Edit |
|---|---|---|---|
| `SocketDispatcher.cs:226-229` CallFailed -> `failed`; `:230` MachineUnreachable -> `unreachable` | 502-like `unreachable` | now `failed` with the same text | none |
| `RemoteFollower.cs:52` | logs, ends follow | uncaught: task faults inside `Task.Run` (:33), the `finally` removes it (:72), error unobserved, no log | catch both (1.5) |
| `RemoteMachine.cs:201` (title refresh) | swallowed | uncaught: flows into `turnEnded` await in `FollowOnceAsync` (:122) then up; follower stops | catch both (1.4) |
| `RemoteOutbox.cs:51` (poll loop) | logs | uncaught: poll loop task dies, outbox never polls again | `catch (Exception error) when (error is MachineUnreachable or CallFailed)` |
| `StartSessionTool.cs:50` (`CreateHelperAsync`) and `:62` (`SendAsync`) | `ToolResult.Error(msg)`; :62 also `SetMessageFailed` | exception escapes the MCP tool | same filter at both |
| `SendMessageTool.cs:44` | `SetMessageFailed` + `ToolResult.Error` | escapes | same filter |
| HTTP, 502 with message: `RowRoutes.cs:57`, `SessionRoutes.cs:82`, `InfoRoutes.cs:41`, `AttachmentRoutes.cs:58`, `RunRoutes.cs:50,86,126,155`, `ConfigRoutes.cs:53` | 502 | unhandled 500 | `catch (Exception error) when (error is MachineUnreachable or Assembly.Application.Common.Calls.CallFailed)` (fully qualified: these files are in `Assembly.Infrastructure.Http`, which has its own `CallFailed`) |

The three Application files (tools) are in `Assembly.Application.Features.Chat.Helpers`; they add `using Assembly.Application.Common.Calls;`. Total one-line edits: 1 + 1 + 1 + 2 + 1 + 9 = 15. Dispatcher and `FakeMachine` need none. `MachineSet.For` (:12) callers keep `MachineUnreachable` for an unknown name: not in the topic, as the note says.

## 4. Tests (red first)

Style reference: `Draft/Base/ClockSocketTests.cs` (real `SocketDispatcher` + `SocketEndpoint` over Kestrel loopback, `CountingTime : FakeTimeProvider` at :~160-190, `Until`, `Change(...)`, `ReadAsync` with 10 s `WaitAsync`), `Draft/Base/Chat/ChatSocketTests.cs:45-70` (how a store, hub, machines and features are wired), `Infrastructure/Remote/RemoteMachineTests.cs:106-123` (a Kestrel stub that stands in for a remote with `MapGet("/version", ...)`).

### 4.1 NEW `Assembly.Tests/Draft/Base/MachinesSocketTests.cs` (~220 lines; `Draft/Base` has 7 files)
Fixture (IAsyncLifetime): `TempDatabase`, `Topics`, `AppDatabase.Open`, `LiveHub(RowStore.Open(db), topics: topics)`, `SessionStore.Open(db, TimeProvider.System)`, `MachineStatus(topics, ["box"])`, **real** `RemoteMachine("box", stub.Address, "t", hub, sessions, TextWriter.Null, status)` (internal; `Assembly.Tests` has InternalsVisibleTo, `I.csproj:12`), `MachineSet(new LocalMachine(hub, sessions, null!, null!)` or a `FakeMachine(null, sessions)` for local, remote)`, `BackendClock(topics, CountingTime)`, `MachinesFeature(machines, status, clock.Start)`, `clock.Ticked += feature.Tick`, register both on the `SocketDispatcher`. Use `FakeMachine(null, sessions)` as local (it is in `Draft/Base/Chat`, namespace `Assembly.Tests.Draft.Base.Chat`).

`StubRemote` (private nested): `WebApplication.CreateSlimBuilder()`, `Listen(IPAddress.Loopback, port)`; routes `GET /version` -> `{"formatVersion":<RowStore.FormatVersion>,"commit":"abc"}` and counts hits in `VersionHits`; `GET /agents` -> `{"agents":[{"id":"claude","name":"Claude"}]}` or, when `AgentsStatus = 500`, `Results.Text("boom", statusCode: 500)`. `StopAsync()` closes the listener. `StartAsync(port)` restarts on the SAME port (first start picks port 0 and remembers it) so `RemoteMachine`'s base address stays valid.

**How the fake remote "stops answering":** `await stub.StopAsync()`. The next request gets connection refused -> `HttpRequestException` -> `MachineUnreachable` at `RemoteMachine.cs:220-222`, instantly (no 30 s wait). Do not rely on a hung handler: that would wait for the real 30 s `HttpClient.Timeout` (:40), and the fake `TimeProvider` does not drive `HttpClient`.

Tests (names exact):
1. `A_call_that_fails_unreachable_sends_the_machines_change_before_its_error_frame`. Sub `machines` -> state frame has local + box (`reachable:false`, S). Call `machines/agents {"machine":"box"}` -> frames: change box `reachable:true,version:"abc"`?? (version only after `/version`; `CheckFormatAsync` reads it at :184 but only the probe stores it, so assert `reachable:true` and `version` per 4.4) then result. `await stub.StopAsync()`. Call agents again -> next frame is the change `reachable:false`, the frame after it contains `"code":"unreachable"`.
2. `A_non_2xx_answer_fails_failed_and_leaves_the_machine_reachable`. Same start (box true). `stub.AgentsStatus = 500`. Call agents -> the NEXT frame is the error with `"code":"failed"` and `answered 500`; no change frame in between. Unsub, sub again: state shows box `reachable:true`.
3. `With_machines_watched_and_a_remote_that_stops_answering_the_15th_tick_probe_marks_it_unreachable`. Sub; `_time.Advance(1 s)` once -> probe (the immediate first one, S): change box true with `version:"abc"` (also proves `commit` is read). `await stub.StopAsync()`; advance 1 s x 14: assert no frame (a short `ReadAsync(...).WaitAsync(200 ms)` expecting `TimeoutException`, as `SteerBackground`-style negative reads; or count `stub.VersionHits` stays 1); advance the 15th: next frame is the change box `reachable:false`.
4. `With_nobody_watching_no_probe_goes_out`. Do not sub. `_time.Advance(1 s)` x 30; assert `stub.VersionHits == 0` and `_time.Timers == 0` (reuse the `Timers` counter, `ClockSocketTests.cs:~165`). Then sub and advance once -> `VersionHits` becomes 1 (waits with `Until`).
5. `A_second_identical_outcome_publishes_nothing`. After box is true, call agents again, then read: the very next frame must be the call's `result` (a change would have been queued before it, per the ordering the note relies on, `SocketDispatcher.cs:230-232` -> same queue). Also stopped-twice: stop stub, two failing calls -> exactly one `reachable:false` change then two error frames.
6. `A_remote_with_another_row_format_is_unreachable_and_does_not_flap` (covers correction 6). Stub `/version` returns `formatVersion:99`. Call agents -> one change `reachable:false`, then `"code":"unreachable"`; call again -> NO second change (only the error frame). Without the `_incompatible` field this test fails with two changes per call.

### 4.2 `Assembly.Tests/Infrastructure/Remote/RemoteTunnelTests.cs` (+~12 lines)
`It_says_exited_each_time_the_command_exits`: pattern of `:28` (`new RemoteTunnel("/bin/sh", ["-c", "exit 3"], log)`); subscribe `tunnel.Exited += () => Interlocked.Increment(ref count)`; `await Until(() => count >= 1)`. (The first exit may precede the subscribe; the supervisor restarts after >= 1 s, `RemoteTunnel.cs:123,147-149`, so >= 1 is still reached.)

### 4.3 `Assembly.Tests/Infrastructure/Remote/RemoteMachineTests.cs` (+~110 lines; file already ~380 lines, it is one class, no limit rule for lines)
All use the existing helpers `StartDesktopAsync`, `Client`, `Json`, `ToolAsync`, `SessionToken`, `FakeLauncher`; the stub is a Kestrel app as at `:108-114`.
- `Ssh_exiting_marks_the_remote_unreachable_and_the_next_answer_marks_it_reachable`: start a desktop with `tunnel: new RemoteTunnel("/bin/sh", ["-c", "sleep 1; exit 3"], TextWriter.Null)` (the box from `_box` is the remote). Open a socket to the desktop (`/socket`, needs the hello frame; copy `ClockSocketTests.OpenAsync`). Sub `machines` (state: box false, S). Call `machines/agents {"machine":"box"}` -> change true. Wait for the change `false` (~1 s later, the tunnel exit). Call agents again -> change true (the next exit is >= 2 s later: backoff 1 s then start then `sleep 1`). Note: `DesktopBackend.StartAsync` already takes `RemoteConfig`, so pass `tunnel` as the new last arg.
- `A_remote_that_answers_500_to_version_does_not_fault_the_follower`: stub with `/version` -> 500; create a `session_info` row with `Machine = "box"` via the desktop's `SessionStore` through `POST /sessions`? No (that calls `/version` and fails with 500). Instead insert with `sessionStore.Add(id, "claude", repoUrl, "box")` through the backend's store (add `internal SessionStore` accessor is NOT allowed: use `GET /sessions/{id}/rows`, which triggers `Watch`, after seeding the row by SQL as `ForgetDelivery` does at the top of this file's helpers, `RemoteMachineTests.cs:~300-330`). Assert `_log` contains `remote session {id}:` ... `answered 500`, and `await desktop.DisposeAsync()` does not throw.
- `Start_session_and_send_message_report_a_500_from_the_remote_as_a_tool_error`: as `:150-171` but the remote is a stub whose `/version` is fine and `POST /sessions` -> 500. Assert `ToolAsync(... "start_session" ...)` returns an error result containing `answered 500` (covers `StartSessionTool.cs:50`). For `:62` (`SendAsync` after create): stub `POST /sessions` -> `{"sessionId":"s1"}` and `POST /sessions/s1/send` -> 500; assert the error result AND that the message row is marked failed (`SetMessageFailed`). For `SendMessageTool.cs:44`: same stub, a prior helper on it, `send_message` to it -> error result.
- `The_outbox_poll_keeps_running_after_a_500` (covers `RemoteOutbox.cs:51`): `RemoteOutbox.PollOnceAsync` is `internal` (:62); build a `RemoteOutbox` over a stub with `/outbox` -> 500, seed one helper row (`HelpersOn`, `RemoteOutbox.cs:~63`), wait for the log to contain `outbox:` twice (loop delay is real 1 s, `:46`, so this test takes ~3 s; acceptable, or call `PollOnceAsync` via `Assert.ThrowsAsync<CallFailed>` to prove the exception type and rely on the filter being a one-liner; recommend the log-twice test).
- `A_title_refresh_that_gets_a_500_is_ignored` (covers `RemoteMachine.cs:201`): stub `/sessions/{id}` -> 500 and a `/live` WebSocket that sends one `turn` row with `state:"completed"` (needs `UseWebSockets`, ~25 lines). Assert the follower keeps following (log has no `remote session` error, session still `Watch`ed). This is the most expensive test: if the budget is tight, cover it by review; state that explicitly in the PR.

### 4.4 Edits to existing tests
- `Draft/Base/Chat/ChatSocketTests.cs:59`: `new MachinesFeature(machines, new MachineStatus(topics, ["box"]), () => { }).Register(calls);` (`topics` is the local at :45).
- `Draft/Base/Chat/ChatSocketTests.Config.cs:59-72` (`Machines_list_names_the_remote_machines_and_agents_answers_a_machines_agents`): delete the `list` lines (:63-64); rename to `Machines_agents_answers_a_machines_agents`; rest unchanged. The other two tests in that file (:77-98) are unaffected.
- `RemoteMachineTests.cs:121` (`BadGateway` for format 99) stays true: `MachineUnreachable` is still thrown at the mismatch (`:186`) and the HTTP route catches it.
- `ClockSocketTests` are unchanged and must still pass (`The_clock_ticks_only_while_someone_is_subscribed`, :80-98): with no `machines` subscriber the timer must still stop on the first unwatched tick.

## 5. Verification commands (for the implementer)
`cd /home/blackcolours/dev/work/worktrees/Assembly/base-machines/dotnet && MSBUILDDISABLENODEREUSE=1 dotnet test Assembly.Tests --filter "FullyQualifiedName~MachinesSocketTests|FullyQualifiedName~RemoteMachineTests|FullyQualifiedName~RemoteTunnelTests|FullyQualifiedName~ChatSocketTests|FullyQualifiedName~ClockSocketTests|FullyQualifiedName~ArchitectureTests"` (check `free -g` first per house rule; one build at a time). Pass = all green; the new tests must have failed first for the right reason (missing type, then missing frame).

## 6. Diff size (honest)
Production, net: `MachineStatus.cs` ~60 new; `MachinesFeature.cs` +45/-10; `RemoteMachine.cs` +30/-3; `RemoteFollower.cs` +6; `RemoteTunnel.cs` +3; `BackendClock.cs` +12/-3; `DesktopBackend.cs` +10/-2; `DesktopServe.cs` +1/-1; `IMachine.cs` +3; `SessionsJson.cs` +1/-2; 15 one-line catch edits across 11 files. About 190 added / 40 removed lines in 20 files, 1 new production file.
Tests: `MachinesSocketTests.cs` ~220 new; `RemoteMachineTests.cs` ~+110; `RemoteTunnelTests.cs` +12; 2 small edits. About 350 new lines, 1 new test file.
Harder than the note says: (a) the stub/real-`RemoteMachine` harness is needed because the unreachable recording lives in `RemoteMachine`, not in `FakeMachine`; (b) the refresh-after-turn and outbox-loop tests need real time or a WebSocket stub; (c) the `ArchitectureTests` rules (`:86` namespace/folder, `:97` feature isolation, `:19` file count) all pass with the files above (assumed, run them).
Rule check against the spec's budget: only one new production file (named in the note); new members are `MachineStatus`, `IMachine.ProbeAsync`, `RemoteTunnel.Exited`, `BackendClock.Ticked`, `RemoteFollower`'s `linked` callback. No other abstractions. If the diff trends past ~2x (about 400 production lines), stop and send a proposal.

## 7. Decision for the controller
Correction 7 (probe at first tick vs every 15th, and initial `reachable:false`) is the only judgment call left; everything else follows the note. Default in this spec: initial false + immediate first probe on subscribe. Tests marked (S) depend on it.
