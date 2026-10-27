Recon, read-only, 2026-10-27, not re-verified unless marked

# Spec: M2 (the `machines` topic, the probe, the tunnel exit), on top of M1

Paths are under `dotnet/`. "Main" line numbers are from the untouched copy /home/blackcolours/dev/work/worktrees/Assembly/base-cut/dotnet (60ce21e; plan says e8e361e). "M1" facts come from reading /home/blackcolours/dev/work/worktrees/Assembly/base-machines at 49be99f plus its uncommitted work (read only). Receipts: V = I read it, A = assumed (check once M1 lands). Nothing built or run.

Abbreviations: App = Assembly.Application, Inf = Assembly.Infrastructure, Desk = Assembly.Desktop, T = Assembly.Tests.

## 0. What M1 looks like (assumptions to check when M1 lands)

- A1 (V, base-machines MachineStatus.cs): `public sealed record MachineItem(string? Name, string Kind, bool Reachable, string? Reason)` and `public sealed class MachineStatus` with ctor `MachineStatus(IEnumerable<string> remotes)`, `IReadOnlyList<MachineItem> Items`, `void Reachable(string machine)`, `void Unreachable(string machine, string reason)`. Body is a stub today (`NotImplementedException`); the real one is A. M2 assumes: both record methods swap an immutable snapshot under a private lock and are no-ops on an identical item; the initial remote is `reachable:false, reason:"not checked yet"` (V in M1 test `MachinesSocketTests.cs`, "not checked yet").
- A2 (V, RemoteMachine diff): `RemoteMachine` ctor takes `MachineStatus status` before `turnEnded`; `CheckFormatAsync` marks Reachable only after the format check passes (`_formatChecked = true; _status.Reachable(_name)`), throws via a `Unreachable(reason, message)` helper on a mismatch; `SendRequestAsync` records "didn't answer" on transport failure and Reachable on any answer except 401/403 (comment "Any answer means the remote is there, unless it refused this computer's token"). Whether an answer sets Reachable when `_formatChecked` is false is A: M2's probe does not depend on it (it goes through `CheckFormatAsync`).
- A3 (V, DesktopBackend diff): `var status = new MachineStatus(remote is null ? [] : [remote.Name]);` right after `sessions`, passed to `RemoteMachine` and `new MachinesFeature(machines, status)`.
- A4 (V, M1 test): `machines/list` result is `{"machines":[{"name":null,"kind":"local","reachable":true,"reason":null}, {"name":"box",...}]}` (nulls ARE written, `Local` constant in MachinesSocketTests.cs). `MachinesFeature.MachineList` is still the result type; M1 gave it `IReadOnlyList<MachineItem>` (A) and registered it in `SessionsJson` (A; main registers `MachinesFeature.MachineList` at SessionsJson.cs:22, V).
- A5: M1 touched the nine route catches, follower catch, outbox, tool catches. M2 does not touch them.
- A6: the 401/403 path and the reason strings ("didn't answer", "the remote refused this computer's token", "not checked yet", format text) already exist in M1. M2 adds one reason: "ssh tunnel exited".

Everything below changes M1's code, so each edit anchor is "M1's version of" the main line.

## 1. Corrections to the note (it is wrong or harder here)

1. **`RemoteTunnel` is `internal`, `DesktopBackend` is `public`** (Inf RemoteTunnel.cs:15 internal sealed, V; Desk DesktopBackend.cs:33 `public sealed class`, V; `RemoteConfig` is public, Inf RemoteConfig.cs:4, V). A public `StartAsync` cannot take a `RemoteTunnel` parameter (CS0051). Fix: make `RemoteTunnel` `public sealed class` (one word). Alternative if Jesse objects: `StartAsync(..., Action<Action>? onTunnelExit)`, which is uglier. The spec uses `public`.
2. **The "never two at once" guard cannot trigger with real timeouts.** Probe timeout is 5 s, the interval is 15 s, so a probe that honours its token is gone 10 s before the next one. The guard only matters if a probe ignores its token. It is still required by the note and is ~3 lines (a `HashSet` of names in flight); test it with a fake machine that ignores its token (section 5, test P4), not with a hanging stub.
3. **"Cancelled when the last subscriber leaves" is observed on the next clock tick (up to 1 s later), not at the unsubscribe.** There is no unsubscribe hook: `SocketConnection` calls `calls.Unsubscribe` (Inf SocketConnection.cs:178, V) which goes straight to `Topics.Unsubscribe` (Topics.cs:51, V). Adding a hook costs more than waiting a tick. But note the clock STOPS on the first tick that finds nobody (BackendClock.cs:56-62, V), so the stop path itself must tell the feature (`Ticked(false)` before it returns), or the probe is never cancelled. Section 3.4.
4. **`ITopics` has no `HasSubscribers(topic)`**, only the keyed one (App ITopics.cs:16, V); the concrete `Topics` has both (Topics.cs:24,30, V). So the "who is watching" check cannot live in `MachinesFeature` (Application). The clock (Infrastructure, holds the concrete `Topics`) does it and passes a bool.
5. **`MachineStatus` needs `ITopics`** to publish, so M1's ctor `MachineStatus(IEnumerable<string>)` changes to `MachineStatus(ITopics, IEnumerable<string>)`. Every M1 test and `DesktopBackend` that constructs it is edited (3 sites, A: MachinesSocketTests.cs:41, ChatSocketTests.cs:59, DesktopBackend).
6. **The ssh-exit test needs a socket client against a real `DesktopBackend`** (status is only reachable through the topic; `GET /machines` over HTTP does not carry `reachable`). That is the one expensive test (section 5, T9); copy `OpenAsync`/`ReadAsync` from MachinesSocketTests.
7. **Probe vs `_formatChecked`.** The probe must re-read the row format every time ("a remote upgraded in place is seen"), but `CheckFormatAsync` returns at once once `_formatChecked` is true (main RemoteMachine.cs:182, V). The probe sets `_formatChecked = false` first, then calls it.
8. **Per-link reachability is lossy.** A follower "link lost" marks the machine unreachable although other sessions' links may be fine, and one session's link settling normally (quiet 2 s, not needed, `RemoteFollower.cs:144`) is NOT a failure. Spec: only the existing "link lost" catch (main RemoteFollower.cs:57-62, V) signals; the normal end does not. Linked-ness for the probe skip is a counter, not that signal.

## 2. Files and edit points (with diff estimates)

### 2.1 `App/Common/Machines/MachineStatus.cs` (M1 file, +~25)
- Ctor `MachineStatus(ITopics topics, IEnumerable<string> remotes)`; keep a `_topics` field.
- At the end of the swap, inside the same lock, when the item changed:
  `_topics.Publish(MachinesFeature.Topic, key, JsonSerializer.Serialize(item, SessionsJson.Default.MachineItem));` with `key = item.Name ?? "local"`. keyedOnly stays false so the keyless subscriber gets it (Topics.cs:20, V).
- Lock order status then Topics (note; `Topics.Publish` takes only its own lock, V Topics.cs:18). The topic's state reads `Items` (the snapshot) with no `_gate`, so no inversion.
- `MachinesFeature.Topic` is in `App.Features.Chat.Sessions`; `MachineStatus` is in `App.Common.Machines`. Main already crosses this way (MachineSet.cs:1 uses the Sessions namespace, V), and `ArchitectureTests` forbids feature-names-another only between features (A, run the test). If it objects, take the topic name as a ctor string instead.

### 2.2 `App/Features/Chat/Sessions/SessionsJson.cs`
- Main :22 `[JsonSerializable(typeof(MachinesFeature.MachineList))]` is deleted with the list call. Add `[JsonSerializable(typeof(MachineItem))]` (+ `using Assembly.Application.Common.Machines;`). Net 0.

### 2.3 `App/Features/Chat/Sessions/MachinesFeature.cs` (+~55 / -8)
Main anchors: ctor :16, `Register` :18-28, records :30-34 (V, base-cut). M1's ctor is `(MachineSet, MachineStatus)`; M2's is `MachinesFeature(MachineSet machines, MachineStatus status, Action startClock, TimeProvider time)`.
- Add `public const string Topic = "machines";` beside `Feature` (same string; the calls `agents` keep `Feature`).
- `Register`: delete the `list` read (main :20-21) and `ListArgs`, `MachineList` records. Add first:
  `calls.Topic(Topic, SessionsJson.Default.MachineItem, item => item.Name ?? "local", (key, _) => { if (key is not null) throw new CallFailed("machines has no keys"); Arm(); startClock(); return status.Items; });`
  Pattern receipts: clock's registration (Inf BackendClock.cs:31-38, V: guard key, `Start()` inside state, return list); `ICallRegistry.Topic` signature (App ICallRegistry.cs:36, V). The keyless sub is allowed (`NeedsKey: false`, SocketDispatcher.cs:93, V).
- Probe logic (all private):
  - `const int Every = 15; static readonly TimeSpan ProbeTimeout = 5 s;`
  - `int _ticks = Every - 1;` (so the first tick after any subscribe probes). `Arm()` does `Volatile.Write(ref _ticks, Every - 1)`; called from the state function (runs under the Topics lock, only writes an int).
  - `readonly HashSet<string> _probing` guarded by `lock (_probing)`; `CancellationTokenSource _stop = new()`.
  - `public void Tick(bool watched)` (called by the clock each second; must never throw):
    ```
    if (!watched) { CancelProbes(); return; }              // CancelProbes: only if _probing.Count > 0: Interlocked.Exchange(ref _stop, new()).Cancel()
    if (Interlocked.Increment(ref _ticks) < Every) return;
    Volatile.Write(ref _ticks, 0);
    foreach (var machine in machines.Remotes)
        if (TryBegin(machine.Name!))  _ = ProbeAsync(machine, _stop.Token);
    ```
    (do not Dispose the replaced CTS: a probe may still hold its token; it holds no timer.)
  - `private async Task ProbeAsync(IMachine machine, CancellationToken stop)`:
    ```
    try {
        using var timeout = new CancellationTokenSource(ProbeTimeout, time);     // fake-time-able; not CancelAfter on a linked source, which uses the real clock
        using var both = CancellationTokenSource.CreateLinkedTokenSource(stop, timeout.Token);
        try { await machine.ProbeAsync(both.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stop.IsCancellationRequested)
            { status.Unreachable(machine.Name!, "didn't answer"); }
        catch (Exception error) when (error is MachineUnreachable or CallFailed) { }   // RemoteMachine recorded it already
    } catch (Exception) { }                                   // a probe never faults the process; a stop-cancel records nothing
    finally { lock (_probing) _probing.Remove(machine.Name!); }
    ```
    `TryBegin` = `lock (_probing) return _probing.Add(name);`.
- Why `Tick(bool)` and not a counter in the clock: the note says "`MachinesFeature` counts its ticks" (V, plan lines 1520-1523).

### 2.4 `App/Common/Machines/IMachine.cs` (+3)
After main :19 `AgentsAsync`, add a default member so `LocalMachine` and `FakeMachine` compile unchanged:
`/// <summary>Asks whether the machine answers, recording the outcome; this machine always does.</summary>`
`Task ProbeAsync(CancellationToken cancellationToken) => Task.CompletedTask;`
(V: `MachineSet.Remotes` is `IReadOnlyList<IMachine>`, MachineSet.cs:9; the feature is in Application and cannot name `RemoteMachine`.) LangVersion latest, net10 (Directory.Build.props:3-4, V): default interface members OK.

### 2.5 `Inf/Remote/RemoteMachine.cs` (+~12)
- New (main has nothing like it):
  ```
  public async Task ProbeAsync(CancellationToken cancellationToken)
  {
      if (_follower.Linked)   // an open link already says the remote is there
          return;
      _formatChecked = false; // re-read the row format: a remote upgraded in place is seen
      await CheckFormatAsync(cancellationToken);   // GET /version; records Reachable / the mismatch / didn't answer (A2)
  }
  ```
  `CheckFormatAsync` main :180-188 (V).
- `_follower` is built at main :42; add the link callback to its ctor args (2.6).
- Add `internal bool Linked => _follower.Linked;` ONLY if the test needs it (T6 does: it waits for the link). One line.

### 2.6 `Inf/Remote/RemoteFollower.cs` (+~12)
Main: primary ctor :18-20, `FollowOnceAsync` :81-134, link-lost catch :57-63, connect :86 (all V).
- Primary ctor: add `Action<bool> linked` after `subagentCopied`.
- `private int _links; public bool Linked => Volatile.Read(ref _links) > 0;`
- After `await socket.ConnectAsync(live.Uri, stopping);` (:86): `Interlocked.Increment(ref _links); linked(true);`
- In the existing `finally` (:129-133) add `Interlocked.Decrement(ref _links);` as its first line (covers settle, close, out-of-order restart, link loss, cancel, because all leave `FollowOnceAsync` through that try; the decrement is guarded by only incrementing once connect returned, so the connect line must sit directly before the `try` at :91; it does).
- Link-lost catch (:57-63): first line `linked(false);`.
- `RemoteMachine` passes `up => { if (up) { if (_formatChecked) _status.Reachable(_name); } else _status.Unreachable(_name, "didn't answer"); }` (connect proves the socket opened; marking Reachable only after the format check keeps the M1 rule A2).

### 2.7 `Inf/Remote/RemoteTunnel.cs` (+3, and `internal` -> `public`)
- :15 `internal sealed class` -> `public sealed class` (correction 1). Check no public member exposes an internal type (`LiveHub` etc. not involved; A, compile).
- After main :40 (`Starts`): `public event Action? Exited;`
- Main :146 (the `_log.WriteLine($"tunnel: ssh exited ...")`): put `Exited?.Invoke();` on the line before it. It runs after `WaitForExitAsync` returned normally (the cancel path `break`s at :144, so a dispose raises nothing; V).

### 2.8 `Inf/Clock/BackendClock.cs` (+~14 / -4)
Main :56-76 tick loop, :41 `Start` (V).
- `Start()` (main :41) from `private` to `internal` (the backend hands it to `MachinesFeature`). Class is already `internal` (:12).
- Add `internal event Action<bool>? Ticked;` (true: `machines` has a subscriber).
- Loop body becomes:
  ```
  lock (_gate) _asked = false;
  var clockWatched = _topics.HasSubscribers(Topic);
  var machinesWatched = _topics.HasSubscribers(MachinesFeature.Topic);
  lock (_gate) { if (!clockWatched && !machinesWatched && !_asked) { _running = false; Ticked?.Invoke(false) /* outside the lock */ ; return; } }
  if (clockWatched) _topics.Publish(Topic, Second, ...);       // main :71, now only for the clock
  Ticked?.Invoke(machinesWatched);
  ```
  Write the stop-path invoke after leaving the lock (do the `Ticked` call in a small `Notify(bool)` that wraps `try { Ticked?.Invoke(x); } catch { }` so a handler cannot kill the loop; main :56-76 only catches `OperationCanceledException`).
- `using Assembly.Application.Features.Chat.Sessions;` for `MachinesFeature.Topic`. Class summary (main :7-11) updated: runs while `clock` or `machines` has a subscriber.
- No new timer; still the one `PeriodicTimer` (main :55).

### 2.9 `Desk/Serve/DesktopBackend.cs` (+~8 / -2)
Main: `StartAsync` :58, topics :62, feature array :90-94 (V). M1 already added `status` (A3).
- Signature: `..., RemoteConfig? remote = null, RemoteTunnel? tunnel = null)` (the 5 other callers use the optional, V: DesktopServe.cs:25, DesktopBackendTests.cs:30, RemoteMachineTests.cs:35,40,115).
- `new MachineStatus(topics, remote is null ? [] : [remote.Name])`.
- Hoist the clock and machines feature above `ApiServer.Build` (:90):
  `var clock = new BackendClock(topics, TimeProvider.System); var machinesFeature = new MachinesFeature(machines, status, clock.Start, TimeProvider.System); clock.Ticked += machinesFeature.Tick;` and use `clock` / `machinesFeature` in the array (main :91, :92). Keep the "clock isn't disposed" comment (:88-89).
- After `machines` is built (main :78-80): `if (tunnel is not null && remote is not null) tunnel.Exited += () => status.Unreachable(remote.Name, "ssh tunnel exited");`
- Dispose order is fine: `DesktopServe` declares `await using stopTunnel` before the backend (DesktopServe.cs:24-25, V) so the backend goes first; a late `Exited` publishes to an empty `Topics`.

### 2.10 `Desk/Serve/DesktopServe.cs` (+1/-1)
Main :25: add `, tunnel` after `remote`. (`tunnel` is null when `--remote` is absent; V, :23.)

## 3. Registration and publish names (summary)
- Registration: `calls.Topic("machines", SessionsJson.Default.MachineItem, item => item.Name ?? "local", (key, _) => ...)` in `MachinesFeature.Register`.
- Publish: `ITopics.Publish("machines", key, json)` from inside `MachineStatus` on a change (App ITopics.cs:13, V). Wire frame: `{"t":"change","sub":N,"op":"upsert","key":"box","item":{"name":"box","kind":"remote","reachable":false,"reason":"didn't answer"}}` (change shape V from ClockSocketTests.cs `Change(...)`; item fields per A4).
- State frame: `{"t":"state","sub":N,"items":[{"key":"local","item":{...}},{"key":"box","item":{...}}]}` (shape V, ClockSocketTests.cs:70).
- The clock-to-feature path: `BackendClock.Ticked(bool machinesWatched)` -> `MachinesFeature.Tick(bool)`.

## 4. Order of work
1. Red tests (section 5) first, each failing for the right reason (missing members, then missing frames).
2. `MachineStatus` ctor + publish, `MachinesFeature` topic + `list` deleted (M1 tests move to the topic).
3. Tunnel event + `DesktopBackend`/`DesktopServe` wiring.
4. Follower link + `RemoteMachine.ProbeAsync`, `IMachine.ProbeAsync`.
5. Clock + `Tick`.
One commit per group is fine; it is one PR.

## 5. Tests (red first)

Shared change: promote `CountingTime` in T `Draft/Base/ClockSocketTests.cs` from `private sealed class` to `internal sealed class` (one word; V it is a private nested class at ~line 168) and use it as `ClockSocketTests.CountingTime`. It counts live timers (`Timers`), so it also proves the 5 s probe-timeout timer is disposed.

### 5.1 `T/Draft/Base/MachinesSocketTests.cs` (M1's file; edit + new tests, +~140)
Fixture changes: `_time = new ClockSocketTests.CountingTime(start)`; `topics` passed to `MachineStatus(topics, ["box"])`; build `new BackendClock(topics, _time)` and `var feature = new MachinesFeature(machines, status, clock.Start, _time); clock.Ticked += feature.Tick; clock.Register(calls); feature.Register(calls);` Dispose the clock in `DisposeAsync`. Stub gets `Hang` (a `TaskCompletionSource` that `/version` awaits, with `RequestAborted`) and `DropVersion` (abort `/version`) next to M1's `Drop`/`Format`.
M1's tests call `machines/list` (`ListAsync`, `List`, `Box`, lines ~118-130 in M1's file). Replace `ListAsync` with: send `sub` on topic `machines` (new id), read the `state` frame, send `unsub`. `List(...)` becomes the state frame `{"t":"state","sub":N,"items":[{"key":"local","item":Local},{"key":"box","item":Box}]}`. Same assertions otherwise (the cases 401/403, 500, transport failure, format 99 remain, now through the topic). Cost: ~20 changed lines.

New tests (names exact):
- T1 `A_machines_subscription_starts_with_every_machine_and_a_remote_not_checked_yet`: sub, state frame = local reachable + box false "not checked yet". Fakes nothing.
- T2 `A_failed_call_sends_the_machines_change_before_its_error_frame`: sub (leave open); call `machines/agents` {"machine":"box"} -> change box true; set `DropVersion`/`Drop` on `/agents`; call again -> next frame is the change box false "didn't answer", the frame after has `"code":"unreachable"`. Remote "stops answering" = the stub aborts the connection (instant `HttpRequestException`, no 30 s wait).
- T3 `A_second_identical_outcome_publishes_nothing`: with box already false, repeat the failing call; the next frame read is the error frame itself (any change would be queued before it, SocketDispatcher.cs:230-232, V per the plan). Same for a repeated success.
- T4 `The_first_tick_after_a_subscribe_probes_the_remote_and_every_15th_after`: sub (box false "not checked yet"); `_time.Advance(1 s)` -> change box true; `VersionHits == 1`. Set `DropVersion`. Advance 1 s x 14: `VersionHits` stays 1 and no frame (`ReadAsync(...).WaitAsync(200 ms)` expects `TimeoutException`). Advance the 15th second: change box false "didn't answer"; `VersionHits == 2`. Advance in 1 s steps (the clock uses `PeriodicTimer` on the fake time; one big `Advance` coalesces ticks).
- T5 `With_nobody_watching_no_probe_goes_out`: no sub; advance 1 s x 30; `VersionHits == 0`, `_time.Timers == 0`. Then sub, advance 1 s, `Until(VersionHits == 1)`.
- T6 `A_remote_with_a_live_follower_link_is_not_probed`: stub also maps `/live` (`UseWebSockets`, accept, send `{"type":"ping"}` every 300 ms until aborted; ~12 lines; pings reset the follower's 2 s settle, RemoteFollower.cs:108,144, V). `_box.WatchRunning(["s1"])`; `Until(() => _box.Linked)`; note `VersionHits` (the format check made one); sub; advance 15 x 1 s; `VersionHits` unchanged. Needs `internal bool Linked` (2.5). The row session need not exist: the follower only needs the socket to open (A: `hub.LastSeq("s1")` of an unknown session is 0; check).
- T7 `A_probe_that_hangs_gives_up_at_5_seconds`: stub `Hang`; sub; advance 1 s (probe starts, `Until(VersionHits == 1)`); advance 1 s x 5 -> change box false "didn't answer" (timeout fired on the fake clock; the real `HttpClient` 30 s is not involved because the probe passes its own token; A: the request token is honoured by `HttpClient.SendAsync`). After it: `Until(_time.Timers == 1)` (only the clock's).
- T8 `The_clock_runs_for_machines_alone_and_stops_when_neither_is_watched`: `Timers == 0`; sub `machines` only (no clock sub) -> `Timers == 1`; advance 1 s -> probe hit (proves it ticks); `unsub`; advance 1 s -> `Until(Timers == 0)`; also `clock` topic frames are NOT sent to a machines-only watcher (no `clock` sub exists, so assert nothing arrives). Then sub `machines` again, advance 1 s: probe hit again (the restart + re-armed first probe). The existing `ClockSocketTests.The_clock_ticks_only_while_someone_is_subscribed` stays unchanged and must pass (V it asserts Timers 0 -> 1 -> 0, ClockSocketTests.cs:80-98).
Edit existing: `A_remote_with_another_row_format_...` (M1) still holds: the mismatch publishes once per distinct reason; `(2, 0)` hit counts are about calls; unaffected unless the probe fires, which it won't (no sub, or advance not called).

### 5.2 NEW `T/Draft/Base/MachineProbeTests.cs` (+~110; folder has 7 files, limit 30, ArchitectureTests.cs:19, V)
No sockets: `MachinesFeature.Tick` driven directly, with a `FakeMachine("far", sessions)` (T `Draft/Base/Chat/FakeMachine.cs`) that gets `public Func<CancellationToken, Task> Probe` and `public int ProbeCalls` plus `public Task ProbeAsync(CancellationToken t)` (it implements the new default member). Time is `ClockSocketTests.CountingTime`; status is a real `MachineStatus(new TopicSet(), ["far"])`.
- P1 `Tick_probes_on_the_first_watched_tick_then_every_15th`: `Tick(true)` -> ProbeCalls 1; 14 more -> 1; the 15th -> 2.
- P2 `Tick_with_nobody_watching_probes_nothing_and_arms_the_next_subscribe`: `Tick(false)` x 40 -> 0; the state function's `Arm()` is covered by T4/T5; here assert `Tick(true)` after unwatched ticks probes at once.
- P3 `A_probe_that_hangs_gives_up_after_5_seconds_on_the_fake_clock`: `Probe = t => Task.Delay(Timeout.Infinite, t)`; `Tick(true)`; advance 5 s; status shows `far` unreachable "didn't answer". Advance 4 s first and assert still reachable-state unchanged.
- P4 `A_probe_still_out_is_not_doubled`: `Probe` ignores its token (awaits a `TaskCompletionSource`); `Tick(true)` then 15 more `Tick(true)` -> `ProbeCalls == 1`; complete the TCS, tick to the 15th again -> 2.
- P5 `The_last_subscriber_leaving_cancels_the_probe_in_flight`: `Probe` records `t.IsCancellationRequested` after `Task.Delay(Infinite, t)` throws; `Tick(true)`; `Tick(false)`; `Until(cancelled)`. And a cancel records nothing: status still "not checked yet".
- P6 `A_probe_that_throws_something_else_does_not_fault`: `Probe` throws `InvalidOperationException`; `Tick(true)`; `Until(!in flight)`; next 15th probes again (the guard flag was cleared in `finally`).

### 5.3 `T/Infrastructure/Remote/RemoteTunnelTests.cs` (+~10)
T10 `It_says_exited_each_time_the_command_exits`: pattern of `:25-33` (V): `new RemoteTunnel("/bin/sh", ["-c", "exit 3"], TextWriter.Null)` subscribe `Exited += () => Interlocked.Increment(ref n)`; `Until(n >= 1)`. The first exit can precede the subscribe; the supervisor restarts after 1 s, so a later exit is seen. Make `Until(n >= 1)` and accept it may take ~1 s (existing `Until`, :40-46, waits 5 s).

### 5.4 `T/Infrastructure/Remote/RemoteMachineTests.cs` (+~60, the expensive one, T9)
T9 `Ssh_exiting_marks_the_remote_unreachable_and_the_next_answer_marks_it_reachable`: `DesktopBackend.StartAsync(..., new RemoteConfig(Box, _box.Address, BoxToken), tunnel: new RemoteTunnel("/bin/sh", ["-c", "sleep 1; exit 3"], TextWriter.Null))` (existing helper pattern: RemoteMachineTests.cs:39-40, V). Open `/socket` with the desktop token (copy `OpenAsync`/`ReadAsync` from MachinesSocketTests), sub `machines`: state box false "not checked yet". Call `machines/agents` {"machine":"workbox-test"} -> change box true. Wait for the change box false "ssh tunnel exited" (~1 s after start). Call agents again -> change box true. The next exit is >= 2 s later (1 s backoff + `sleep 1`), so the second call wins the race; if flaky, lengthen the `sleep`.
Edit: M1's `RemoteMachineTests` that construct `DesktopBackend.StartAsync` need no change (optional last parameter).

### 5.5 Other edits
- `T/Draft/Base/Chat/ChatSocketTests.Config.cs` (main :59-72, V): delete the `machines/list` lines (M1 already edited them to carry `reachable`; M2 deletes them), rename `Machines_list_names_the_remote_machines_and_agents_answers_a_machines_agents` -> `Machines_agents_answers_a_machines_agents`.
- `T/Draft/Base/Chat/ChatSocketTests.cs:59` (V main): `new MachineStatus(topics, ["box"])`, and `new MachinesFeature(machines, status, () => { }, TimeProvider.System)`.
- `T/Draft/Base/Chat/FakeMachine.cs`: the probe members above (+5).

## 6. Verification command (verbatim)
```
cd /home/blackcolours/dev/work/worktrees/Assembly/base-machines/dotnet && free -g && MSBUILDDISABLENODEREUSE=1 dotnet test Assembly.Tests --filter "FullyQualifiedName~MachinesSocketTests|FullyQualifiedName~MachineProbeTests|FullyQualifiedName~RemoteMachineTests|FullyQualifiedName~RemoteTunnelTests|FullyQualifiedName~ChatSocketTests|FullyQualifiedName~ClockSocketTests|FullyQualifiedName~ArchitectureTests"
```
(Run in the M2 branch's own worktree, not M1's. `free -g` available > 6 GB first.) Pass: all green, the new tests seen red first. Then `rg -n "machines/list" dotnet app` must find only the deleted-call history, none live (the app page may call it; if the page still calls `machines/list`, M2 is not done: A, grep `app/src` before starting).

## 7. Diff size (honest)
- Production: `MachineStatus` +25, `MachinesFeature` +55/-8, `IMachine` +3, `RemoteMachine` +12, `RemoteFollower` +12, `RemoteTunnel` +3, `BackendClock` +14/-4, `DesktopBackend` +8/-2, `DesktopServe` +1/-1, `SessionsJson` +1/-1. About 135 added / 15 removed, 10 files, 0 new production files (the note names none).
- Tests: `MachinesSocketTests` +140 (and ~20 changed), new `MachineProbeTests` +110, `RemoteMachineTests` +60, `RemoteTunnelTests` +10, `FakeMachine` +5, 4 one-line edits. About 330 added, 1 new test file.
- Under 2x of a ~200-line production estimate; no new abstraction beyond: `IMachine.ProbeAsync`, `RemoteTunnel.Exited`, `BackendClock.Ticked`, `RemoteFollower.Linked` and its callback.

## 8. Risks to watch
- Fake-time determinism: `PeriodicTimer` on `FakeTimeProvider` coalesces a multi-second `Advance` into one tick; always advance 1 s per step and wait for the effect (`Until`) between steps, as ClockSocketTests does (V ClockSocketTests.cs:90-98).
- `new CancellationTokenSource(ProbeTimeout, time)` makes a fake timer per probe (counted by `CountingTime`); it is disposed by the `using` in `ProbeAsync`. T5/T7 assert it.
- Tick runs on the timer's continuation thread; `Tick` only increments an int, takes the tiny `_probing` lock, and starts a task. It must not await.
- `Ticked(false)` also fires every second while only `clock` is watched; `CancelProbes` is a no-op when nothing is in flight, so there is no allocation churn.
- If M1's `MachineStatus` already has a different publish hook (e.g. an event), reuse it instead of the `ITopics` ctor argument; the rest of M2 is unchanged.
