# What a GPUI window needs from the backend when /socket drops or the backend restarts

Read-only mapping, 2026-10-27, on branch claude/project-thread-mpas6r (after `DropAll`, App Nap hold). Paths are under `dotnet/`.

A drop and a restart look the same to the window: sub and call ids are per socket, nothing is kept for a closed
socket, and the token and seqs survive a restart. No "was it a restart" signal is needed.

## 1. Topic or call, behaviour on reconnect, backend gap

| Topic / call | On reconnect | Gap |
| --- | --- | --- |
| `rows` sub with `after=N` (RowsFeature.cs:48-54, SocketDispatcher.cs:104-114) | State frame `seq` = N, then each row after N once (prevSeq chain, RowStore.cs:124-133), then live. Rows already in the snapshot are not repeated (SocketDispatcher.cs:111-113). | None. The catch-up falls back to `state.seq = last` with no rows when N is past last, more than 500 rows, or over 8 MB (RowsFeature.cs:51-54). The window sees that as `state.seq != N`, a gap. |
| `rows` first open (no `after`) | State `seq = last`, no rows. History comes from `rows/page`. | None. The window merges page and live rows by item key. |
| `rows/page`, `rows/tool`, other reads | A closing socket cancels running reads (SocketDispatcher.cs:220-222). The answer is lost. | None. The window asks again if it still wants it. |
| `sessions` sub (SessionsFeature.cs:34-37) | Full state again. Only the newest 200 when unkeyed (ListLimit). | The window must upsert the state, not replace its map. There is no remove op (SocketDispatcher.cs:314). |
| `preferences` sub, saves (PreferencesFeature.cs:26-32, SocketDispatcher.cs:71-97) | State is every preference. A save in flight commits even if the socket closed (SocketDispatcher.cs:219), but its echo and reply are lost. | None. The new state holds the committed value, which is why 5.9 resets the counts. |
| `clock` sub (BackendClock.cs:32-38) | State is the current time. The old subscription goes at close (SocketConnection.cs:65-67), so the timer stops within a second of the last socket. Missed ticks do not matter. | None. |
| `send/send` in flight (SendFeature.cs:36-45) | Commits and runs on the machine; the result is lost. No client id, so a resend sends the prompt twice. | See 3. The window must not resend. |
| `send/stop`, `send/answer` | Repeats fail harmlessly: "isn't running" or "isn't open" (SendFeature.cs:50,59). | None. |
| `sessions/create` in flight (SessionsFeature.cs:57-69) | Commits; the session reaches the window through the `sessions` state or change. A resend makes a second session. | The window must not resend. |
| `sessions/read`, `config/set` | Idempotent. | None. |
| `attachments/begin`, `chunk` (AttachmentsFeature.cs:55-107) | An upload belongs to the socket that began it (Find, :158-162). Closing drops it and its staging file after its queued writes run (:147-154). A chunk on the new socket gets "unknown upload". | None. The window starts over with `begin`. |
| `attachments/commit` in flight (:108-125) | Runs to the end and saves the image on the session's machine. The path in the reply is lost. | The saved image is orphaned; a retry saves it again. Accepted: disk only. |
| `attachments/read` | Read; lost with the socket. | None. |
| `diagnostics/*` | `drop` ends every socket, the caller's included (SocketEndpoint.cs:416,425). | None. |

Writes on a new socket do not order against writes still draining on the old one (the order key and queue are per
socket, SocketConnection.cs:29,87-103). For saves this is microseconds. For a `send` still running on a machine (up to
30 s) the window just must not send again on the same session until the outcome is known.

## 2. Restart differences

- Token: the same, since the token file is kept (TokenFile.cs:14-24). Deleting the file gives a new token and the window's hello closes with 4401.
- Seqs: continue, since they live in SQLite. A window's `after` stays valid. A database that was wiped or restored
  gives `after > last`, which falls back to `state.seq = last` and is a gap.
- Subscriptions, uploads, running calls: all gone. Staging files are deleted at startup, only Guid-named ones (AttachmentsFeature.cs:44-49).
- Runs: `RunRecovery.CloseUnfinished` runs in `StartAsync`, before Kestrel listens (DesktopBackend.cs:83-88; RunRecovery.cs:25-48). It closes every local turn left open with an error row (`backend_stopped`) and `turn: failed`, a tool still running failed, an ask cancelled, tasks stopped.
  Those rows are already saved before the "listening on" line (DesktopServe.cs:29), so the window's first catch-up gets them.
  Remote sessions' turns stay open until the remote follower reports (`WatchRunning`, DesktopBackend.cs:99).
- App Nap: reset, then set from the summaries (DesktopBackend.cs:88).
- Port: whatever the launcher passes; the window must take the port from the launcher each time.

## 3. Backend gaps and smallest fixes

None are required for a correct reconnect. Candidates, all optional and not built:

1. A send's unknown outcome: "not landed yet" and "never landed" look the same while the old write runs. No fix; the window shows "not confirmed". A `clientId` on `send/send` would settle it; not in the spec.
2. Orphaned attachment after a lost `commit` reply. No fix.
3. No keep-alive options are set (ApiServer.cs:37). Fine on loopback.

## 4. Failing tests for the gaps

None needed; RowsSocketTests.cs, SocketTests.Drop.cs and ChatSocketTests.Attachments.cs already cover the backend half
(reconnect after a drop, the three catch-up fallbacks, drop ends every socket, uploads die with their socket). One test
is missing: `A_resume_after_a_restart_gets_the_recovery_rows` (restart, then a sub with the old seq gets the closing rows).

## 5. What the window lanes must do

g0-socket:
- Keep each rows subscription's watermark: the last seq applied. Send it as `after` on every re-sub, with the same key.
- Re-subscribe every sub after `welcome`, with fresh sub ids. Treat topic `state` as upserts.
- Gap when `state.seq != after` (not on a first open), or a row's `prevSeq != watermark`. Then drop that session's held rows, load the newest `rows/page`, and continue from `state.seq`. Ignore a row with `seq <= watermark`.
- On close: settle every pending call as aborted, reset save counts, hold nothing. Never resend a `send/send`, `sessions/create` or `attachments/commit`; restart an upload from `begin`.
- Reopen once, then wait for the launcher's ready event; no timers. A 4401 close means re-read the token file (launcher), not loop. 4400 stops.

g0-host:
- Wait for the `listening on` stdout line, with its own limit and exit watch, and keep reading stdout after it.
- Restart only on the Reconnect button and only if the child has exited. After the new line, give the window the port and token again and fire ready.
- On quit, SIGTERM and wait; the backend drains queued writes (plan 5.9).

## 6. Open questions

- Should a dropped `send/send` get a `clientId` so the window can tell landed from lost? (Backend change, owner decision.)
- A backend alive but wedged (accepts nothing): the Reconnect button does nothing since the child has not exited. Should the launcher offer a kill and restart of its own child?
- On a gap, is reloading the newest page enough for a long-running task row older than the page? (Assumed yes.)
