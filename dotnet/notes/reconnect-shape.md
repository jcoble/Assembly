# Reconnect, backend-ready and the App Nap hold: the backend's half (shape, before code)

Updated after the pre-implementation check (2026-10-27): its changes are folded in below.

The GPUI switch plan's backend work after rows moved onto `/socket`: "reconnect plus a backend-ready signal", and its
B8 note that the App Nap hold "moves into the backend, held while any turn runs, with no window calls". The window's
half (reopening once, waiting for the launcher's restart, "Disconnected" with its Reconnect button) is G0's
`g0-socket` and `g0-host` lanes. This lane changes only `dotnet/`, plus deleting the page's two App Nap calls with the
routes they call.

## 1. Reconnect

What a reconnecting window needs from the backend is mostly there already: a closing socket ends its subscriptions and
cancels its reads, its queued writes still commit, and a new socket that subscribes to `rows` with `after` = its last
seq gets only what came after (no duplicates). What's missing is a way to make the backend drop the socket, so the
reconnect can be tested end to end and from the soak.

- `diagnostics/drop` ends every `/socket` connection too, not only the `/live` row subscriptions. The caller's
  socket is among them, so its answer may not arrive; the window treats the end as any other drop. A drop also
  cancels a read the receive loop is busy with (a sub's state), and a socket being accepted as it happens.
- `SocketEndpoint` gets `DropAll()`: it cancels a drop token every open socket is linked to (beside the stopping one,
  which stays as it is, so a drop during shutdown can't undo it) and puts a fresh one in its place, so sockets opened
  after the drop work. The old source isn't disposed: a socket accepted mid-swap may still read its token.
- The cancelled receive aborts the socket, so the client sees a reset, not a close frame, as with a lost connection.
  The tests accept either.
- `DiagnosticsFeature` takes one `Action drop` as today; `DesktopBackend` passes one that runs both
  `hub.CloseSubscriptions` and the endpoint's `DropAll`, which `ApiServer.Build` hands out as `out Action dropSockets`
  (the endpoint is internal).

Failing tests first (`Assembly.Tests/Infrastructure/Socket/SocketTests.Drop.cs`):

- `Drop_ends_every_open_socket`: two hello'd sockets; `DropAll()`; both end.
- `A_socket_opened_after_a_drop_works`: drop, then a new socket gets its welcome and a call answers.
- A write queued before a drop still commits: already covered by
  `Closing_the_socket_cancels_running_reads_and_queued_writes_still_commit`, since a drop goes through the same close.
- `DesktopBackendTests.The_desktops_drop_ends_every_open_socket` (it replaces the test that expected drop's answer).

And one end-to-end test on the rows topic (`Draft/Base/RowsSocketTests.cs`):

- `A_reconnect_after_a_drop_gets_only_the_rows_it_missed`: subscribe, read to seq N, drop, two rows are published
  while no socket is open, a new socket subscribes with `after` = N and gets exactly those two, once each.

## 2. Backend-ready

The launcher (g0-host) starts `Assembly.Desktop serve --port P --home H --token-file F` and today polls the port. The
backend already prints `listening on http://127.0.0.1:P` on stdout once Kestrel listens. That line becomes the
contract: the launcher waits for it instead of polling, and it is printed only once the socket is ready for a hello
and the MCP url is set. The token stays in the token file and is never printed. A restart is the launcher's (the
user's Reconnect button when the child has exited); the backend needs nothing new for it, since a fresh start already
closes the turns the last run left open (`RunRecovery`).

No backend code changes: the line is printed after `StartAsync` returns, which is after Kestrel listens, `/socket` is
mapped and the MCP url is set, so it is never early. A comment at the line says so. No test: `MainAsync` waits for
Ctrl+C and shares `Console.Out` with other writers, so a test would pin more than the contract.

For g0-host: the line never comes when the remote tunnel fails, its token read hangs, or the port is in use, so the
launcher keeps watching for the child's exit, with its own limit on the wait. It must keep reading stdout after the
line (or send it to a file), or the backend blocks on a full pipe.

## 3. The App Nap hold, in the backend

Today the page calls `activity.begin` / `activity.end` (HTTP `PageRoutes`, from `Conversation.svelte:147-148`; the
socket has the same two calls with no caller). They can run out of order, and one flag means two sessions release
each other's hold.

- The backend holds the activity while any session has an open run, local or remote (a remote turn streams through
  this backend, so it must not be napped either). The open-run fact already has one owner: `SessionSummaries` from
  PR #24 (`SessionSummary.RunId`), updated under the hub's lock on every saved turn row. The hold reads it: after each
  summary change, held = any session has a `RunId`. No second tracker of open runs.
- `TurnActivity` stays the macOS call, still idempotent, and returns early unless `OperatingSystem.IsMacOS()`: once
  rows drive it, it runs inside `LiveHub.Push` on every machine.
- `SessionSummaries` gets `AnyRunOpen`. `IDiagnosticsHost` gets `HoldActivity(bool)` in place of
  `BeginActivity`/`EndActivity`. `DesktopBackend` wires it: the hub's `summaryChanged` also calls
  `HoldActivity(summaries.AnyRunOpen)`, and it is called once after recovery, since loading the summaries tells
  nobody (a remote run left open at startup holds it). No new class.
- Deleted together: the socket's `activity.begin` and `activity.end` with their test and fake, `PageRoutes`' two
  cases and its `_activity` field, and `Conversation.svelte`'s two `pageCall`s. Safe while the Tauri page runs, since
  the backend holds it on its own.
- A remote run whose end row never arrives (the remote is unreachable) keeps the hold. Accepted.
- This part waits for PR #24 to merge (it needs `SessionSummaries`); parts 1 and 2 don't.

Failing tests first: a turn's start row holds it and its end row releases it; two sessions' turns, the first ending
keeps it held; a remote session's copied start row holds it; a backend started with an unfinished remote run holds it.

## Size

Part 1: about 60 lines of code and 120 of tests. Part 2: a test and a comment. Part 3: about 40 lines of code, 80 of
tests, about 30 deleted.
