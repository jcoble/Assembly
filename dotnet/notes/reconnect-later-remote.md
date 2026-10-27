# Reconnect: the backend's link to a remote machine

Paths under `dotnet/`, at head c111ac0. Read only; nothing built or run.

## 1. Today

- **One machine, from a flag.** `DesktopServe.cs:23` awaits `RemoteTunnel.ConnectAsync` before the backend exists; the tunnel isn't passed on (`DesktopBackend.cs:392`). `MachineSet` is fixed at construction (`MachineSet.cs:46`). No saved list, no add/remove, no user disconnect.
- **Tunnel.** `SuperviseAsync` (`RemoteTunnel.cs:121-157`) restarts a dead ssh forever, 1 s doubling to 30 s, on `Task.Delay`. ssh keepalive ends a dead link in about 45 s (`:58`).
- **Calls.** Transport failure becomes `MachineUnreachable` (`RemoteMachine.cs:406-417`); routes answer 502, the socket answers `unreachable` with the machine name (`SocketDispatcher.cs:238-240`).
- **Rows.** `RemoteFollower` opens `/live?after=hub.LastSeq` (`:85`) and copies with `hub.PublishCopy` (`:116`; it refuses a gap and reopens). A dropped socket retries 1 to 30 s while a run is open or a webview watches (`:36,57-62`). `Watch` runs on send, open, and at startup for runs left open (`RemoteMachine.cs:302,363`; `DesktopBackend.cs:428`).
- **Outbox** polls each second, logs unreachable and carries on (`RemoteOutbox.cs:208-216`).
- **Window.** Sees only the `unreachable` error frame. `machines/list` is names only (`MachinesFeature.cs:21`).

## 2. Gaps against the doc line

1. **A down host at startup kills the backend.** `ConnectAsync` throws (`RemoteTunnel.cs:73-74,114`), `MainAsync` fails, and local sessions are gone too.
2. **No saved machines**, only `--remote`.
3. **Split ownership.** Connect is one-shot; backoff is private to the tunnel; the token and `_formatChecked` (`RemoteMachine.cs:375`) are read once, so a changed token or an upgraded remote never recovers.
4. **The follower gives up.** `FollowAsync` returns on `MachineUnreachable` (`:52-56`), e.g. when the first `/version` check fails at startup; nothing restarts it.
5. **No user disconnect.**
6. **Real clock.** Tunnel, follower and outbox use `Task.Delay`, so tests can't drive them and "retries run on the backend clock" (plan ~1621) is unmet.
7. **Cached sessions look live.** Remote turns stay open (`RunRecovery` skips them, `DesktopBackend.cs:413`) and nothing says disconnected.

## 3. Smallest design

**Relation to `lane/base-machines`** (6b232c0): it adds `MachineStatus` (`Reachable`/`Unreachable(reason)`), `reachable` on `machines/list` (M1), then the `machines` topic, probe and tunnel-exit hook (M2). It retries nothing. Reconnect only calls `MachineStatus`; no second flag. Build on M1; the tunnel passed into `DesktopBackend` is M2's wiring, so take it rather than add it twice.

**One owner: `MachineConnection`**, one new file `Infrastructure/Remote/MachineConnection.cs` (~120 lines), per machine name:

- **Loop.** Connect = start tunnel, read token, wait for the port, `GET /version`. Failure: mark `Unreachable(reason)`, wait, retry. Success: mark `Reachable` and wait for a drop (ssh exit, or a call or follower failure), then run the chain again. Re-reading the token each time fixes gap 3; reset `_formatChecked` too.
- **Timing.** Backoff is `Task.Delay(backoff, timeProvider, token)` on the `TimeProvider` given to `DesktopBackend`. 1 s doubling to 30 s, reset after a 30 s hold. `RemoteTunnel` stops owning backoff and just reports "ssh exited". No front-end timer.
- **Cancel.** `DisconnectAsync(name)` cancels one token, stopping the wait and any connect in flight (give `WaitForPortAsync` a token; `ReadTokenAsync` has one), disposes the tunnel (`:167`), stops the machine's followers and outbox, and marks `Unreachable("disconnected")`. Backend dispose uses the same path. Nothing retries after it.
- **Startup.** `DesktopServe` starts the connection and the backend at once. Machines begin `Unreachable("not checked yet")`; a cached session shows disconnected until the first `/version` answers with the token.
- **Followers resume.** After a connect, `WatchRunning` for sessions whose rows show an open run plus any a webview watches. The follower already opens `/live?after=LastSeq`, so no protocol change. Make the follower retry on `MachineUnreachable` (backoff, while `Needed`) instead of returning.
- **One attempt per machine.** A second connect for a connecting or connected name is a no-op.

Size: ~150 new, ~60 changed (`DesktopServe`, `DesktopBackend`, `RemoteTunnel`, `RemoteFollower`, `RemoteMachine`), ~250 tests. No other new type.

## 4. Failing tests (fake `TimeProvider`, fake ssh command, stub HTTP; in `Assembly.Tests/Infrastructure/Remote/`)

1. `A_down_host_at_startup_does_not_stop_the_backend`: the backend starts and local calls work.
2. `A_machine_that_cannot_connect_retries_with_backoff_on_the_clock`: attempts at 1, 2, 4 ... 30 s of fake time, none between.
3. `A_connect_marks_reachable_after_version_answers`: not when ssh starts.
4. `A_dropped_tunnel_reconnects_and_reads_the_token_again`: the new token is used.
5. `Disconnect_stops_attempts_and_the_tunnel`: no ssh start in an hour of fake time, process gone, status unreachable.
6. `Disconnect_during_a_connect_stops_it`: a hung port wait ends, no process left.
7. `Followers_resume_from_the_last_seq_after_a_reconnect`: the second `/live` has `after=` the last copied seq; no duplicate row.
8. `A_follower_that_finds_the_remote_down_tries_again`: red today (`RemoteFollower.cs:52`).
9. `Reconnect_checks_the_row_format_again`.
10. `Only_one_attempt_runs_per_machine`.

## 5. Questions for Jesse

1. Is saved-machines storage (list, add/remove while running) in this lane? Smallest version restores only `--remote`.
2. While disconnected, do remote turns stay open (today) and catch up on return, or show "lost" after a time?
3. Does a user disconnect leave a running remote turn running there? I assume yes.
4. Keep retrying forever on a refused token (401/403)? I'd keep going, since the token is re-read.

## 6. Risks

- Overlap with base-machines in `RemoteMachine.cs`, `DesktopBackend.cs` and the tunnel hook; sequence after M1/M2.
- C2 (one socket per remote) rewrites `RemoteMachine`/`RemoteFollower`; keep `MachineConnection` independent of them.
- A token read per attempt is one ssh process per try, at most every 30 s.
- A mid-turn restart can't lose rows silently: `PublishCopy` refuses gaps (test 7 covers it).
- The tunnel's local port is remote port + 10000 (`RemoteTunnel.cs:69`); two machines on one remote port would clash.
