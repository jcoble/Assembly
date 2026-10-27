# g0-socket: the lane's plan and notes

Lane g0-socket (G0 steps 3 and 4): the window's one socket client in `desktop/src/core/socket/`, the backend clock's
subscriber in `core/clock/`, and the state entities in `core/state/`. Step 1 (files, names and types, no logic) is
the commit that adds this file. Step 2 brings the logic, test first, against a real backend on the lane's port.

## Decisions from the pre-implementation check (2026-10-27)

- Threads (a change from the brief's "one background thread", reviewed and kept): one thread can't block on both a
  socket and a channel without a timeout or a poll loop, which the rules forbid. So one client thread blocks on one `Event` channel with no timeout; each open connection has a reader thread
  that blocks on the socket. The writer is a second tungstenite `WebSocket` on a clone of the stream. The backend
  sends no pings (no KeepAlive in ApiServer.cs), so the reader never writes except a close reply. Frames and closes
  carry a connection number so a dropped connection's late events are ignored. Dropping a connection shuts the
  stream down, which ends its reader.
- Calls and subscriptions share one id counter: an `error` with a sub's id drops that subscription and ends its
  updates. A re-subscribe on the same socket sends `unsub` first (the backend answers "already open" otherwise).
- `cancelled` settles a call `Aborted`, never `Failed`, so `ErrorCode` has no `Cancelled`.
- A call made while `Disconnected` fails at once, as on the page (socket.ts:123-126).
- Rows: a row with `seq <= last` is skipped; a row whose `prevSeq` isn't the last seq re-subscribes with `after` =
  last. A `state` whose `seq` isn't the `after` sent (too far behind, past the end, or a wiped database) is a
  `RowUpdate::Jump`: g0-session drops what it holds and reloads with `rows/page`, `before = seq + 1`.
- Machines: main answers names only. `Machine` has `name` now; `reachable` is added when the base lane's M1 merges
  (no optional field as a fallback). M2 then turns `machines/list` into a topic.
- `reconnect(endpoint)` takes the endpoint each time, and the launcher reads the token file afresh for it: a
  restarted backend may have a new port, or a new token if its token file was deleted. So a hello refused with 4401
  (bad token) needs no handling of its own: it's a failed open, `Disconnected`, and the next `reconnect` brings the
  current token. 4400 (wrong protocol) is a build mismatch and also just shows `Disconnected`.
- State changes reach the window through an unbounded channel, handed to the client thread when it starts and read
  on the foreground, which updates the `SocketClient` entity; it emits `ConnectionState`.
- An entity's subscription is owned by the task that reads its updates; dropping the task unsubscribes.
- Every call sends `args: {}` at least, never null (a null answers `bad_args`, SocketDispatcher.cs:201).
- A first rows open (no `after`) gets `state.seq = last` with no rows, which reads as a `Jump`: the session view
  loads its newest page then, the same as after a far-behind reconnect.

The backend lane's window contract is dotnet/notes/reconnect-later-window.md on its branch (PR #27): never resend
`send/send`, `sessions/create` or `attachments/commit` after a drop; restart an upload from `begin`.

## What step 2 ports

**Client (app/src/lib/socket.ts)**
- Hello: token plus protocol 1, ready on `welcome` (:57-66), whose `machine` feeds `Connected`. Bad token closes 4401 (SocketConnection.cs:144-147).
- Result settles Ok. `cancelled` settles Aborted, quietly. Other codes settle Failed (:69-75).
- Latest wins: a newer keyed call cancels the older and aborts it at once. If the older was still waiting for the socket, it's never sent (:109-114, :157-165).
- Owner drop sends `cancel`, and the call settles on the backend's answer (:151-155).
- Calls and subs share one id counter (:29, :114, :221). An error on a sub id drops that sub (:234-240).
- `state` and `change` upsert, with no removes yet (:23, :204-215).
- Reconnect: a welcomed close aborts every pending call, reopens once, and re-sends every sub (:63-64, :77-100). The page has no Disconnected state yet; the Rust shape adds it.
- Error codes are listed in SocketFrames.cs:22-27. Only `unreachable` carries `machine`.
- Save is out of scope.

**Rows.** The page still reads rows over `/live` (rows/client.ts:24-47). Its gap rule, `liveStep`, is in rows/store.ts:155-159:
- `seq <= watermark`: ignore.
- `prevSeq == watermark`: apply.
- anything else: reopen from the watermark.

On the socket: `sub` with `after` answers `state{seq:S, items:[]}`, then `rows{sub,row}` frames (SocketDispatcher.cs:323-368). A catch-up over 500 rows or 8 MB jumps to the last seq with no rows (RowsFeature.cs:48-54).

**Sessions**
- `sessions` topic: the newest 200, upserted on every change (SessionsFeature.cs:18, :31-34).
- `sessions/page {before, beforeId, limit}` answers `{sessions, nextBefore, nextBeforeId}` (:35-38, :59-61). The state has no cursor, so the client takes it from its oldest item.
- The backend already keeps helpers off the list (SessionStore.cs:227, keyedOnly :313-317). The page opens a child separately (+page.svelte:43-55).

**Machines**
- Main: `machines/list` answers `{machines:[name]}` (MachinesFeature.cs:20-21).
- M1 (base-machines, not merged): each item becomes `{name, kind, reachable, reason}` (MachineStatus.cs:4, MachinesSocketTests.cs:152-155).
- M2 replaces `machines/list` with a topic (implementation-plan.md:1531-1549).

**Clock.** `clock`/`second` gives `{now}` in Unix ms. It runs only while subscribed, and any other key fails (BackendClock.cs:31-37).

**Drop.** On main, `diagnostics/drop` only closes `/live` subscriptions. The reconnect lane's commit 4f603fc (not merged) makes it end every socket (SocketTests.Drop.cs:9-35). Its "listening on" line is the ready signal (DesktopServe.cs:26-29).

The behavior list is docs/behavior/g0-socket.md.

## Step 2 tests

**Setup.** The guard scans every desktop/*.rs, tests included (guard.sh:93), so tests can't sleep or use `recv_timeout`; every wait is a blocking `recv()`.
- One backend per test binary: `Assembly.Desktop serve --port $DESKTOP_PORT --home <tmp> --token-file <tmp>/env --agents <tmp>/agents.json`.
  - The port is 18867 for lane 7 (lane-env.sh:18).
  - Startup waits for the "listening on " line.
  - The token is read from `ASSEMBLY_TOKEN=` (TokenFile.cs).
  - The backend is stopped by its saved PID.
- A stub agent in agents.json lets `sessions/create` seed sessions and helpers, since it only writes a row (LocalMachine.cs:20-24).

| Test | Asserts | Pins |
|---|---|---|
| hello_gets_welcome | Connected with the machine name | #1 |
| wrong_token_disconnected | Disconnected, no retry loop | #1, #8 |
| call_result_and_not_found | Ok; Failed(NotFound) | #2 |
| newer_keyed_call_aborts_older | older Aborted, newer Ok | #3 |
| dropped_call_cancels | later calls work and nothing settles twice (weak, Q4) | #4 |
| sub_state_change_unsub | state, then an upsert after create; nothing after unsub | #5, #11 |
| unknown_topic_drops_sub | NotFound reaches the subscriber | #5 |
| drop_reconnects_resubscribes | pending Aborted, then Connected and state again | #7 |
| backend_down_then_ready | Disconnected; after restart and Reconnect, Connected | #8 |
| rows_resub_after_last_seq | the re-sub carries after = last seq (Q2) | #9 |
| rows_feed_gap_check | pure logic: ignore, apply, resubscribe | #9 |
| newest_200_then_pages | 230 seeded: 200, then 30 with no duplicates | #10 |
| helper_off_list | absent from the list, seen by a keyed sub | #12 |
| selection_survives_upserts | the id is unchanged after reorders | #13 |
| machines_reachable | local item reachable | #14 |
| clock_only_while_held | `now`, then a tick; nothing after drop | #15 |

## Open questions and risks

1. Reconnect tests need 4f603fc, which isn't merged. Wait for it, or base on it?
2. Rows need a turn. Who supplies the fake ACP agent?
3. `unreachable` needs a remote. Use Workbox Test as `--remote`, or prove it only in the four-way check?
4. There's no slow call, so "cancel reaches the backend" can't be shown. Add a test-only call?
5. Machines: decided, `name` now and `reachable` with M1.
6. A call while Disconnected: decided, it fails at once.
7. A rows jump: decided, `RowUpdate::Jump { seq }` tells g0-session to reload its newest page.
8. The token comes from the token file, not stdout (DesktopServe.cs:31): the brief says stdout; the launcher (g0-host) reads the file.
9. With no test timeouts, a missing event hangs CI. Use a nextest slow-timeout?
10. Upserts can grow the list past 200. Trim it?
11. Should `send/send` carry a window-made id, so after a reconnect the window can tell a send that landed from one that was lost? A backend change; Jesse decides (reconnect-later-window.md section 6).
