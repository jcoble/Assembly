# Step 3, slice 1: a remote session's rows arrive on the machine socket

Written 2026-10-29 by the controller (Claude) from `remote-socket-recon.md` section 7. Dispatch to a Sol 6.1 medium
lane on agent-workbox **after PR #108 (lane/machines) merges**, because #108 rewrites how `RemoteMachine` is made
(`RemoteConnections.cs`), and this slice must land on top of it, not beside it. Base the lane on main at that time.

## What changes, in one sentence

The desktop stops opening one `/live` WebSocket per followed remote session and instead subscribes to the remote
backend's `rows` topic over the one machine socket it already holds, with `after` set to the desktop's last copied
sequence, so a reconnect catches up without missing or repeating a row.

## Behaviour pinned

- remote#18 An open remote chat streams new rows in order while its turn runs.
- remote#20 After reconnecting, remote chats catch up with missed rows in order, without missing or repeating rows.
- remote#8 (the push half) session changes arrive as pushes, without polling.

Tests cite these as `// Pins: remote#20` (Draft tests under `dotnet/Assembly.Tests/Draft/Remote/` while the list is
a draft; `scripts/pin.sh remote` moves them once Jesse corrects the list).

## Files (exact; no new source files unless the controller approves)

- `dotnet/Assembly.Infrastructure/Remote/RemoteMachine.cs` (309 lines): owns the live machine socket (a
  `ClientWebSocket` to `<tunnel>/socket` with the bearer token, the `hello`/`welcome` handshake as `SocketFrames.cs`
  defines it) and exposes subscribe/unsubscribe to a topic with a key and `after`. Its HTTP calls stay in this slice.
- `dotnet/Assembly.Infrastructure/Remote/RemoteFollower.cs` (287 lines): replaces its `/live` client
  (`RemoteFollower.cs:160–171`) and reconnect backoff (`RemoteFollower.cs:96–137,154–157`) with a `rows`
  subscription on the machine socket, `after` = the last copied seq for that session. Row frames feed the same copy
  path (`LiveHub.PublishCopy`) as today. A dropped socket is one event: the machine's socket closed; the follower
  re-subscribes when `RemoteMachine` reports the socket open again (driven by the tunnel's exit/restart events,
  `RemoteTunnel.cs:44–45,156–163`), with no timer of its own.
- `dotnet/Assembly.Remote/Serve/ServeRun.cs` (69 lines): registers `RowsFeature` on the remote host's `/socket`
  (today its socket registers no chat features, `ServeRun.cs:54–60`), so `rows` with `after` and `rows/page` answer
  there exactly as on the desktop (`RowsFeature.cs:51–68`).
- Tests: `dotnet/Assembly.Tests/Draft/Remote/RemoteRowsSocketTests.cs` (new test file; allowed) plus edits to
  `dotnet/Assembly.Tests/Infrastructure/Remote/RemoteMachineTests.cs` and `RemoteMachineTests.Restart.cs` where
  they drive `/live` directly (`RemoteMachineTests.cs:43–145`, `Restart.cs:130–165`).
- `scripts/features/live-rows.ts` stays: it tests the desktop's `/live`, which the frozen page still uses, not the
  machine link.

## Not in this slice (and deleted later, not kept as a fallback)

- The server-side `LiveSocket` route and the HTTP chat routes stay until the `app/` cut (frozen-page note,
  `implementation-plan.md:1447–1454`). Only the desktop's `/live` **client** in `RemoteFollower` is deleted here.
- `/version`, sessions, send, config, tool details, attachments and the outbox move in slices 2 to 4.
- No new transport abstraction, no interface over the socket, no retry/caching layer, no config option. Plan C2
  names `RemoteMachine` as the socket client; keep it there.

## Failing tests first (names fixed)

1. `Remote_rows_arrive_on_the_machine_socket_while_a_turn_runs` — pins remote#18: with a fake remote host serving
   `/socket` with `RowsFeature`, a followed session's new rows reach `LiveHub.PublishCopy` in seq order; no
   `/live` connection is opened (assert the fake's `/live` was never hit).
2. `Remote_rows_catch_up_after_the_socket_drops_without_a_gap_or_a_repeat` — pins remote#20: drop the socket after
   seq 5, publish seqs 6 to 9 on the remote, reopen; the desktop copies exactly 6 to 9 once.
3. `Remote_follower_has_no_timer_of_its_own` — reconnect is driven by the socket-open event only: with a
   `FakeTimeProvider` advanced by an hour and no open event, nothing reconnects; one open event reconnects.

## Verification, verbatim

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/Assembly.Tests --filter "FullyQualifiedName~Remote"
```
Passing means every test in that filter passes, and the three above failed before the change (paste both runs).
Then the four-way check from the lane folder (`dotnet/scripts/workbox.sh serve <branch>`, then start the desktop
backend with `--remote workbox-test --remote-port $BOX_PORT --remote-token-file $BOX_TOKEN_FILE`, then
`scripts/check4.sh`), committed as `Evidence: four-way check on <short> (rows on the machine socket) passes`.
Then a feature check in the real app on Workbox Test: open a remote session, send a prompt, watch rows stream; stop
the lane's box unit mid-turn and start it again; the chat catches up. One screenshot of the caught-up chat.

## Size and scope

About 200 to 300 changed lines across the three source files and the tests. Needing twice that, or a file not
named above, means stop and send the controller a one-paragraph proposal. KISS and YAGNI as in AGENTS.md "Scope".
No AGENTS.md or CLAUDE.md edits. Commit messages end with `Architecture: unchanged` (or update
`docs/architecture.md` if the machine link's description there changes) and `Committed-by: Codex`.

## Amendment (2026-10-29)

The controller permits `RemoteConnections.cs` to signal a newly reachable machine after `_machines.Set(remote)`, or `RemoteTunnel.cs` to expose `Ready` only if that is simpler, and permits migrating or deleting the old `/live` fixtures in `Infrastructure/Remote/RemoteFollowerTests.cs` and `Draft/Base/MachinesSocketTests.Stub.cs` and `MachinesSocketTests.cs`. Because a backend restart can leave the SSH tunnel connected, `RemoteMachine` owns one cancellable reconnect wait using its supplied `TimeProvider`, with the existing backoff; followers have no retry timer. The final amendment replaces repeated catch-up subscriptions: an empty catch-up with a newer seq triggers backward `rows/page` reads from `last + 1` to the last copied seq, then copies in seq order and continues the live subscription; add `Remote_rows_page_a_gap_too_big_to_push` (remote#20), leave `RowsFeature.cs` and `RowsSocketTests.cs` unchanged, and keep the total change around 400 to 500 lines in the named files.
