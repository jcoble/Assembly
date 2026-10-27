# Reconnect, backend-ready and the App Nap hold: the backend's half (shape, before code)

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

- `diagnostics/drop` closes every `/socket` connection too, not only the `/live` row subscriptions. The caller's
  socket is among them, so its answer may not arrive; the window treats the close as any other drop.
- `SocketEndpoint` gets `DropAll()`: it cancels the token every open socket is linked to and puts a fresh one in its
  place, so sockets opened after the drop work. `CloseAll()` (the host stopping) stays as it is.
- `DiagnosticsFeature` takes one `Action drop` as today; `DesktopBackend` passes one that runs both
  `hub.CloseSubscriptions` and the endpoint's `DropAll`. `ApiServer.Build` hands the endpoint out for that.

Failing tests first (`Assembly.Tests/Infrastructure/Socket/SocketTests.Drop.cs`):

- `Drop_closes_every_open_socket`: two hello'd sockets; `DropAll()`; both see a close.
- `A_socket_opened_after_a_drop_works`: drop, then a new socket gets its welcome and a call answers.
- `A_write_queued_before_a_drop_still_commits`: a save in flight when the socket drops still lands.

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

No backend code changes here unless the pre-check finds the line can come early. One test pins it: the line is the
last thing `DesktopServe` writes before waiting, after `StartAsync` returns (read from a captured writer).

## 3. The App Nap hold, in the backend

Today the page calls `activity.begin` / `activity.end` (HTTP `PageRoutes`, from `Conversation.svelte:147-148`; the
socket has the same two calls with no caller). They can run out of order, and one flag means two sessions release
each other's hold.

- The backend holds the activity while any session has an open run, local or remote (a remote turn streams through
  this backend, so it must not be napped either). The open-run fact already has one owner: `SessionSummaries` from
  PR #24 (`SessionSummary.RunId`), updated under the hub's lock on every saved turn row. The hold reads it: after each
  summary change, held = any session has a `RunId`. No second tracker of open runs.
- `TurnActivity` stays the macOS call, still idempotent; something small in `Features/Diagnostics` turns "any run
  open" into `Begin`/`End` on each change (only when it flips). On Linux it does nothing, as today.
- Deleted together: the socket's `activity.begin` and `activity.end`, `IDiagnosticsHost.BeginActivity`/`EndActivity`,
  `PageRoutes`' two cases, and `Conversation.svelte`'s two `pageCall`s.
- This part waits for PR #24 to merge (it needs `SessionSummaries`); parts 1 and 2 don't.

Failing tests first: a turn's start row holds it and its end row releases it; two sessions' turns, the first ending
keeps it held; a remote session's copied start row holds it; a backend started with an unfinished remote run holds it;
the socket no longer answers `activity.begin`.

## Size

Part 1: about 60 lines of code and 120 of tests. Part 2: a test and a comment. Part 3: about 40 lines of code, 80 of
tests, about 30 deleted.
