# S1: `send/send` takes an optional clientId, so a retried send never starts a second turn

Base lane, 2026-10-27. Line numbers are on this branch (`lane/base-s1`) after the fix; paths are under `dotnet/`.

## Why

The window, a reconnecting socket, and the desktop calling a remote can all send the same message twice: the first
answer is lost, so the sender tries again. Before this change each retry started a new turn, or steered the running
turn a second time. Now a sender can name each message with a `clientId`, and a retry with the same id gets the first
send's run id back. That holds across a socket reconnect, a backend restart, and the desktop to remote hop.

## The shape

- `SendFeature.SendArgs` has `string? ClientId = null` (`Assembly.Application/Features/Chat/Sessions/SendFeature.cs:87`).
  The answer is still `Sent(RunId)`.
- The clientId is passed along the whole path:
  - `IMachine.SendAsync(session, text, clientId, ct)` (`Assembly.Application/Common/Machines/IMachine.cs:33`);
  - `LocalMachine` passes it to `SessionHost.SteerAsync(text, clientId, ct)` (`Assembly.Infrastructure/Machines/LocalMachine.cs:26-27`);
  - `RemoteMachine` puts it in the POST `/sessions/{id}/send` body when it has one (`Assembly.Infrastructure/Remote/RemoteMachine.cs:107-113`);
  - `RunRoutes` reads an optional string `clientId` and passes it on (`Assembly.Infrastructure/Http/RunRoutes.cs:109-132`).
  - The other callers pass null: `StartSessionTool.cs:61`, `HelperDelivery.cs:22` (behind `SendMessageTool`) and
    `RemoteOutbox.cs:107`.
- `TurnStarted` and `TurnSteered` each have `string? ClientId = null` (`Assembly.Domain/Events/`). RowConverter writes
  `UserPayload(Text, From, ClientId)` on both user-row paths (`RowConverter.cs:85` for a steer, `:142` for a new turn).
- There is exactly one check, in `SessionHost.SteerAsync` (`SessionHost.cs:132-174`), on the machine that owns the
  session. The socket path and the remote's HTTP path both go through it. With no clientId, nothing changes
  (`SessionHost.cs:134-135`).
- `TurnSteered` also carries the run id of the turn the message was steered into (see ruling 1 below).

## The record, and a restart

- The stored user rows are the record. Every send with a clientId writes it on its user row, for a new turn
  (`SessionHost.cs:118`) and for a steer (`SessionHost.cs:230`).
- `SessionHost` keeps `_sending`, a map by clientId under `_gate`, for sends still in flight only
  (`SessionHost.cs:43`). An entry is added under `_gate` before the first await, and removed once that send is done
  and its row is written (`SessionHost.cs:177-194`). There is no size limit and no eviction. Each entry holds the
  send's text as well as its task, for the text check in change 5.
- The check, under `_gate` (`SessionHost.cs:138-156`):
  1. If the map has the id, wait on that same task (see change 2 and ruling 2). The host logs
     `send <clientId>: joined in-flight send` when a retry joins (`SessionHost.cs:144`).
  2. Otherwise, if a stored user row in this session has the id, answer that row's run id.
  3. Otherwise start the send and add it to the map.
- A retry after a restart finds the row and answers that run id. RunRecovery has already closed the run as
  failed/backend_stopped if it was still running, so no second turn starts.
- Scope is per session. The same clientId in another session starts its own turn.

## Steer

- A steer's user row has run_id = the running turn, so a retry of a steer that landed answers the same run id with no
  second steer.
- A retry while the first steer is still waiting on the agent waits on the same task and gets the same answer: the
  run id, or null, which fails the call the way the first one failed.
- promptRequired: the retry waits on the same task, and the new turn's user row carries the clientId
  (`SessionHost.cs:235-236`).

## Remote

- The remote owns the session and does the check: RunRoutes, then its LocalMachine, then SessionHost.
- The desktop's copy of the user row carries the clientId too, since it copies the remote's stored payload as is.
- Known limit: a remote on an older build ignores clientId.

## Storage and lookup

- `UserPayload` has `string? ClientId = null` (`Assembly.Domain/Rows/UserPayload.cs:7`). Rows are written with
  `WhenWritingNull`, the same way `From` is left out, so old rows don't change. No schema change and no FormatVersion
  bump.
- The lookup is `Schema.UserRowOfClient` (`Assembly.Application/Features/Chat/ChatSchema.cs:81-93`; bounded in the PR
  review round, item D), read through
  `RowStore.UserRowOfClient` (`RowStore.cs:201-204`) and `LiveHub.UserRowOfClient` (`LiveHub.cs:135-139`):
  `SELECT run_id, json_extract(payload, '$.text') FROM rows INDEXED BY rows_item WHERE session_id = $session AND item_id IS NULL AND kind = 'user' AND json_extract(payload, '$.clientId') = $client ORDER BY seq DESC LIMIT 1`.
  It also reads the row's text, for the text check in change 5.
- No new index. `sessions/create` gets no clientId for now.

## Changes after review

1. A cross-socket race test against a real SessionHost: two sockets send the same clientId at once and there is one
   turn; and a reconnected socket resends while the first steer is still held at the agent, and gets the first's
   answer with one steer (`ChatSocketTests.Send.cs`, on the real machine in `ChatSocketTests.Real.cs`).
2. A retry waits with its own token (`sending.WaitAsync(cancellationToken)`, `SessionHost.cs:163`), so cancelling the
   retry never cancels the first send. What a retry does when the first send fails is narrowed by ruling 2 below.
3. The promptRequired path no longer waits on the turn without a token: `await turn.Done.WaitAsync(cancellationToken)`
   (`SessionHost.cs:235`).
4. An EXPLAIN QUERY PLAN test that the lookup is one search of `rows_item` with no sort (`RowStoreTests.cs`). Timing
   on a copy of the 40k test database (385 sessions, 46,978 rows; the lane's own test data, not the owner's): the
   session with the most user rows is `big-session-40k`, with 5,000. Its seeded user rows carry item ids, which real
   user rows never do, so on the copy they were given no item, as the converter writes them. A lookup that finds
   nothing walks all 5,000: 5 to 7 ms. A hit stops at the newest match.
5. The same clientId with different text answers `failed` "clientId already used for another message" and writes one
   line starting `Warning:` to the backend log (`SessionHost.cs:197-203`). The same clientId with the same text
   answers the original run id.
6. A clientId must be 1 to 64 characters of `[A-Za-z0-9_-]` (`SendFeature.ClientIdError`, `SendFeature.cs:80-84`).
   Anything else answers `failed` "clientId must be 1 to 64 letters, digits, - or _" on the socket
   (`SendFeature.cs:42-43`), and 400 with `{"error": ...}` on the remote route, as RunRoutes answers any bad body
   (`RunRoutes.cs:109-114`).

## Fix round rulings

1. A steer the agent answers "injected" after its turn ended was still delivered. The host writes its user row
   anyway, with the clientId, on that turn's run, and the send answers that run id (`SessionHost.cs:228-232`).
   `TurnSteered` carries the run id (`Assembly.Domain/Events/TurnSteered.cs`), because the converter's own idea of the
   running turn may already be a newer one. RowConverter puts the row on that run, and closes the open message and
   thinking only when that run is the one still running (`RowConverter.cs:81-91`).
2. A retry that joined a send which then failed, timed out or was cancelled sends nothing. It looks at the rows once:
   a row with the clientId answers that row's run id; otherwise it answers the same failure (`SessionHost.cs:165-175`;
   a cut-off is reworded in the PR review round, item B).
   Only a send that arrives after the map entry is gone does the row lookup and then a fresh send.
3. A resend with other text through the desktop to a remote reaches the socket as `failed` with the message
   `workbox-test answered 502: {"error":"clientId already used for another message"}`. It contains the same words as
   a local send, so it was left as it is; `RemoteMachineTests.A_resend_through_the_desktop_with_other_text_fails_with_the_remotes_words`
   keeps it so.
4. The tests prove the wait path: the "while held" part of the steer test and the cut-off test wait for the host's
   `joined in-flight send` line, on a test log that signals when a line is written (no sleeps, no polling).

## PR review round

- A. The remote's send route no longer cancels a send when the desktop's request drops (a tunnel blip, the Mac
  asleep). Like a write on the socket (plan 5.9, `SocketDispatcher.cs:219-222`), it runs on a token bounded by the
  socket's call limit, `SocketDispatcher.Limit` (30 s), on the backend's TimeProvider, and not linked to
  `RequestAborted` (`RunRoutes.cs:133-140`; ApiServer passes `TimeProvider.System`). A steer that reached the agent
  waits for its answer and writes its row, so the window's retry through the desktop answers that run, and the agent
  is steered once. A send that runs past the limit answers 502 with the cancel's message.
- B. A retry that joined a send which was then cut off, with no row written, fails with `failed` "this message's
  first send was cut off; it may have reached the agent" (`SessionHost.cs:172-173`) instead of rethrowing the cancel,
  which the socket answered "send/send failed" and logged as an error. Other failures still rethrow.
- C. A JSON null clientId on the remote route counts as no clientId, as on the socket (`RunRoutes.cs:112-113`).
- D. The lookup reads only the session's newest 200 user rows (`ChatSchema.cs:81-93`, the constant `ClientIdRows`):
  a real resend comes within seconds, or soon after a restart. Every window send carries a clientId, so nearly every
  lookup is a miss, and it runs under SessionHost's lock and LiveHub's, which every session's publishes share. The
  plan is still one search of rows_item with no temporary sort. On a fresh copy of the 40k test database (user rows
  given no item, as the converter writes them), a miss on `big-session-40k` (5,000 user rows) takes about 0.1 to
  0.4 ms of CPU, under 1 ms; the unbounded query, timed on the same copy, takes about 5 ms (5 to 7 ms before).

## Known limits

- A send cut off at 30 s after its request went out may be steered twice if it's sent again after the failure: the
  map entry is gone and no row was written, so the new send goes ahead. Since item A this applies only to the 30 s
  limit itself, not to a dropped desktop request, which was the commonest way a remote send got cut off.
- A clientId older than the session's newest 200 user rows isn't found, so a resend that late starts a new send.
- A late "injected" that arrives after the cut-off writes no row: the cut-off send has stopped listening for it.
- A remote on an older build ignores clientId.
- Through the desktop, a remote's refusal of a resend with other text keeps the remote's words, but inside the
  desktop's "workbox-test answered 502: {...}" wrapper, so the words are not exactly a local send's.
- A steer the agent injects late, after a new turn has already started, gets its row tagged with the old run but
  placed after the new turn's rows.
