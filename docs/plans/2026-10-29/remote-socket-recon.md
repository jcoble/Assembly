# Recon: remote over the socket (plan step 3)

Written 2026-10-29 by a Luna 6 (max) read-only lane on agent-workbox, at main `b5c70f23`, for the controller to turn into step-3 specs. Receipts are `file:line` at that commit; "assumed" marks what the lane did not verify. It also reads the diffs of PR #108 (lane/machines) and PR #111 (lane/board), which touch the same code.

## Summary

- `Assembly.Remote` provides the socket endpoints, but its current feature list is empty for chat; the desktop registers the chat features (`ApiServer.cs:54–70`, `ServeRun.cs:54–60`, `DesktopBackend.cs:153–164`).
- The desktop calls remote HTTP routes through `RemoteMachine`; `RemoteFollower` separately streams rows over one `/live` WebSocket per session (`RemoteMachine.cs:64–174`, `RemoteFollower.cs:160–171`).
- `RemoteOutbox` polls every second, and usually fetches messages every five seconds while helpers are idle (`RemoteOutbox.cs:41–73`, `RemoteOutbox.cs:86–87`).
- Row copying is still needed to populate the desktop’s local history; freshness for remote session summaries needs a remote push or another explicit follow rule (`RemoteFollower.cs:13–16`, `implementation-plan.md:1510–1514`).
- The plan’s route deletion needs sequencing: the frozen-page note keeps `/live` and chat HTTP routes until the app cut, and `check4.sh` still tests `/live` (`implementation-plan.md:1447–1454`, `scripts/features/live-rows.ts:7–11`).
- The local `origin/lane/machines` snapshot adds saved machines but still constructs the HTTP-based `RemoteMachine` and `RemoteOutbox` (`origin/lane/machines:dotnet/Assembly.Infrastructure/Remote/RemoteConnections.cs:107–117`).
- The local `origin/lane/board` snapshot adds a separate Board socket; C2’s “one live socket” needs to include or explicitly exclude it (`origin/lane/board:dotnet/Assembly.Infrastructure/Workflows/RemoteWorkflowMirror.cs:14–16`, `implementation-plan.md:1763–1771`).

## 1. Inventory: desktop-to-remote HTTP and socket traffic

`ApiServer` maps the same route classes for the desktop and remote hosts, and separately creates `/socket` and `/socket/bulk` when given a writer and topics (`ApiServer.cs:39–70`). The HTTP clients listed below are calls from the desktop backend’s `RemoteMachine` to the remote backend (`RemoteMachine.cs:64–174`). HTTP requests are single request/response calls; `/live` streams rows. The only polling in this inventory is the desktop’s repeated `GET /outbox` (`RemoteOutbox.cs:41–73`).

| Remote endpoint | Server route and client call | Payload and behavior | Feature |
|---|---|---|---|
| `GET /version` | Server: `InfoRoutes.cs:21–24,63–70`. Client: `RemoteMachine.cs:210–220`. | Row format version and build commit; one-shot compatibility check before copying rows. | Info / row-format compatibility. |
| `GET /agents` | Server: `InfoRoutes.cs:25,28–60`. Client: `RemoteMachine.cs:64–69`. | Agent IDs and names; one-shot list request. | Info / machine agent list. |
| `POST /sessions` | Server: `SessionRoutes.cs:19–24,52–93`. Client: `RemoteMachine.cs:71–86`. | Creates a session with agent and repository; helper creation also carries parent session and title. | Sessions. |
| `GET /sessions/{id}` | Server: `SessionRoutes.cs:22,45–50,96–108`. Client: `RemoteMachine.cs:224–239`. | Session metadata; fetched once after a followed turn ends to refresh title and activity. | Sessions. |
| `GET /outbox` | Server: `OutboxRoutes.cs:18–20,25–41`. Client: `RemoteMachine.cs:89–93`, polled by `RemoteOutbox.cs:63–73`. | Snapshot of helper messages waiting for delivery, with message ID, from, to, text and creation time. The endpoint itself is one-shot; the desktop polls it. | Remote helper outbox. |
| `POST /outbox/{id}` | Server: `OutboxRoutes.cs:21,43–56`. Client: `RemoteMachine.cs:95–99`, called by `RemoteOutbox.cs:89–120`. | Delivery result: run ID or failure reason. | Remote helper outbox. |
| `POST /outbox/{id}/answer` | Server: `OutboxRoutes.cs:22,58–68`. Client: `RemoteMachine.cs:100–105`, called by `RemoteOutbox.cs:76–82`. | Parent answer state, reply and optional error. | Remote helper outbox. |
| `POST /sessions/{id}/send` | Server: `RunRoutes.cs:23–25,103–170`. Client: `RemoteMachine.cs:107–119`. | Text plus optional client ID; returns run ID or conflict. Rows stream separately. | Send. |
| `POST /runs/{id}/stop` | Server: `RunRoutes.cs:25–26,172–185`. Client: `RemoteMachine.cs:121–128`. | Stops a run and reports whether it was stopped. | Send / stop. |
| `POST /sessions/{id}/asks/{ask}` | Server: `RunRoutes.cs:27,67–100`. Client: `RemoteMachine.cs:156–164`. | JSON answer for an open ask; reports conflict if the ask no longer accepts it. | Send / answer. |
| `POST /sessions/{id}/tasks/{task}/stop` | Server: `RunRoutes.cs:28,31–65`. Client: `RemoteMachine.cs:166–173`. | Stops background work or a remote helper run. | Send / stop task. |
| `GET /sessions/{id}/tools/{item}` | Server: `RowRoutes.cs:17–21,49–79`. Client: `RemoteMachine.cs:133–140`. | Tool summary, input, output, diff and path; one-shot detail read. | Rows / tool details. |
| `POST /sessions/{id}/attachments` | Server: `AttachmentRoutes.cs:20–24,26–70`. Client: `RemoteMachine.cs:142–148`. | Raw image bytes with content type; accepts images up to 20 MB and returns the saved path. | Attachments. |
| `GET /sessions/{id}/config` | Server: `ConfigRoutes.cs:19–23,25–27`. Client: `RemoteMachine.cs:150–152`. | Current agent options, choices and commands. | Config. |
| `POST /sessions/{id}/config` | Server: `ConfigRoutes.cs:21–22,28–38`. Client: `RemoteMachine.cs:153–155`. | Config ID and value; returns updated config. | Config. |
| `WS /live?session=…&after=…` | Server: `ApiServer.cs:48–53`, `LiveSocket.cs:14–16,23–40`. Client: `RemoteFollower.cs:160–171`. | Backlog after the supplied sequence, then live row frames; sends a ping after 30 seconds of quiet. This is a stream, not a poll. | Rows / live delivery. |

The remote-to-desktop direction is implemented as the desktop polling and acknowledging the remote’s outbox, while the remote sends its rows over the WebSocket opened by the desktop (`RemoteOutbox.cs:12–16,63–83`, `RemoteFollower.cs:160–171`). I found no remote-initiated HTTP caller in these paths; the desktop’s remote client methods are all in `RemoteMachine` (`RemoteMachine.cs:64–174`).

Other HTTP routes exist but are not calls made by `RemoteMachine`: for example, `GET /machines`, `GET /sessions`, and `GET /sessions/{id}/rows` are mapped by `InfoRoutes`, `SessionRoutes` and `RowRoutes` respectively (`InfoRoutes.cs:21–26`, `SessionRoutes.cs:19–24`, `RowRoutes.cs:17–21`). `RemoteMachine` has no corresponding calls for them (`RemoteMachine.cs:64–174`).

## 2. What the window-to-desktop socket can provide for the machine link

- Frames are JSON text messages. The incoming frame carries the type and optional token, protocol, call ID, feature, method, arguments, topic, key and `after`; welcome, result and error frames are already defined (`SocketFrames.cs:6–30`).
- The first frame is `hello` with protocol and token. The server replies with `welcome` carrying protocol and machine name; the token stays out of the URL (`SocketConnection.cs:143–167`, `implementation-plan.md:389–400`).
- `/socket` and `/socket/bulk` use the same dispatcher. Bulk calls are kept separate from live calls and topics (`ApiServer.cs:54–70`, `SocketEndpoint.cs:7–13`, `ICallRegistry.cs:9–11`).
- The dispatcher resolves the feature and method, parses arguments, applies the 30-second call limit and returns result or structured error frames (`SocketDispatcher.cs:22–37,205–262`).
- Topics send an initial state followed by changes. Subscription state and registration happen under the same topic lock so changes are not lost between the snapshot and subscription (`SocketDispatcher.cs:120–166`, `Topics.cs:36–48`).
- The `rows` topic is keyed by session and accepts `after`; it sends a sequence state, catches up from stored rows, then streams later rows. The rows feature opens the machine watch for that session (`ICallRegistry.cs:39–44`, `RowsFeature.cs:51–68`).
- Cancellation is a `cancel` frame; the server cancels the handler token. Closing the connection cancels reads but leaves queued writes to finish (`SocketConnection.cs:194–198`, `SocketDispatcher.cs:225–230`). The plan explicitly says a forwarded remote call sends `cancel` on the machine socket (`implementation-plan.md:488–498`).
- Frames are limited to 4 MB and queued output to 16 MB per connection (`SocketConnection.cs:13–22,218–230,254–270`). The bulk path is already designed for larger uploads as chunked `attachments/*` calls, rather than a single 20 MB frame (`implementation-plan.md:386–388`, `ChatSocketTests.Attachments.cs:12–31`).
- A socket write commits through `IAtomicWriter`; the writer commits before publishing subscribed changes (`SocketDispatcher.cs:39–68`, `implementation-plan.md:581–595`). A write may also supply a `failed` callback for cleanup when the call answers with an error, cancellation or timeout (`ICallRegistry.cs:13–22`).
- The window’s Rust `SocketClient` already shares call/subscription IDs, supports calls and topic subscriptions, and exposes row subscriptions with an `after` cursor (`desktop/src/core/socket/client.rs:27–35,134–188`). Its connection sends the token and protocol during hello and treats a failed send as a dropped connection (`desktop/src/core/socket/connection.rs:42–90,93–112`).
- There is not yet a shared .NET machine socket client in the audited chat path: `RemoteMachine` owns an `HttpClient` and `RemoteFollower` owns a separate `ClientWebSocket` (`RemoteMachine.cs:24–46`, `RemoteFollower.cs:163–168`). The Board branch’s `RemoteWorkflowMirror` is a separate bespoke client (`origin/lane/board:dotnet/Assembly.Infrastructure/Workflows/RemoteWorkflowMirror.cs:14–16,39–60`).

## 3. Gaps and socket equivalents

| Existing HTTP path | Socket equivalent and required change |
|---|---|
| `/version` | No equivalent is specified in C2. `welcome` only includes protocol and machine name, while `RemoteMachine` currently checks the row format version before copying rows (`SocketFrames.cs:10–14`, `RemoteMachine.cs:208–220`). **Assumed proposal:** add a small version result to the existing socket handshake or a call before row subscription; the exact contract needs a decision. |
| `/agents` | Use the existing `machines/agents` call; C1 names it and `MachinesFeature` registers it (`implementation-plan.md:1463–1468`, `MachinesFeature.cs:87–92`). The remote host must register that feature on `/socket`; it does not currently register chat features (`ServeRun.cs:54–60`, `ApiServer.cs:54–60`). |
| `POST /sessions` | Use `sessions/create`, which already creates remote sessions and helpers through the selected machine (`SessionsFeature.cs:77–89`). The remote host still needs this feature registered on its socket (`ServeRun.cs:54–60`). |
| `GET /sessions/{id}` and title refresh | Replace the one-time metadata read with the keyed `sessions` topic, which can send the current item and subsequent changes (`RemoteMachine.cs:224–239`, `SessionsFeature.cs:33–38`). |
| Send, stop, answer and stop-task routes | Use `send/send`, `send/stop`, `send/answer` and `send/stopTask`; those calls already forward through the session’s machine and cancellation token (`implementation-plan.md:1463–1468`, `SendFeature.cs:34–72`). |
| Config GET and POST | Use `config/get` and `config/set`, already registered to call the session’s machine (`implementation-plan.md:1466–1468`, `ConfigFeature.cs:28–43`). |
| Tool details | Use `rows/tool` on `/socket/bulk`, already implemented as a bulk call and capped for large output (`RowsFeature.cs:28–30,78–89`, `ICallRegistry.cs:9–11`). |
| Attachment upload | Use the chunked `attachments/*` calls on `/socket/bulk`; the desktop feature exists and the plan assigns attachment work separately (`implementation-plan.md:1466–1468`, `ChatSocketTests.Attachments.cs:12–31`). The remote host must register it as well (`ServeRun.cs:54–60`). |
| `/live` rows stream | Replace the per-session `ClientWebSocket` with a `rows` topic subscription using the desktop’s last sequence as `after` (`RemoteFollower.cs:163–171`, `RowsFeature.cs:51–68`). C2 already describes this shape (`implementation-plan.md:1763–1771`). |
| Outbox GET and acknowledgement POSTs | Replace polling with a remote outbox topic and calls for delivery result and answer; C2 says polling becomes a subscription but does not name its topic or call contract (`RemoteOutbox.cs:63–83`, `implementation-plan.md:1763–1767`). **Assumed:** the topic and acknowledgement calls can be added to the existing chat feature registration without adding a transport abstraction. |

Row copying remains necessary: the desktop stores copied rows and serves older history from its own copy (`RemoteFollower.cs:13–16`, `implementation-plan.md:1768–1771`). The remote session summary is a separate freshness gap: the current desktop loads `SessionSummaries` and publishes changes through its hub, but `Assembly.Remote` creates a plain `LiveHub` and does not configure summaries or register `SessionsFeature` (`DesktopBackend.cs:108–117`, `ServeRun.cs:41–60`). C2’s remote sessions-topic relay could push summary changes, but the plan also says remote summaries based only on copied rows are a separate decision (`implementation-plan.md:1473–1478,1510–1514`).

**Plan conflict:** C2 says to delete the chat HTTP routes and `LiveSocket` in C2, while the frozen-page note says those routes and `/live` stay until the app cut (`implementation-plan.md:1447–1454,1772–1773`). This matters because `ApiServer` maps the same HTTP chat routes on both hosts (`ApiServer.cs:39–53`).

## 4. Polling, timers, retries and sleeps on the machine path

| Current wait or retry | Evidence and effect | Event or replacement |
|---|---|---|
| Outbox loop: one-second delay | `RemoteOutbox.PollAsync` delays one second before every poll (`RemoteOutbox.cs:41–60`). | Replace message discovery with a remote outbox topic change; remove this loop (`implementation-plan.md:1763–1767`). |
| Idle outbox cadence: five ticks | While helpers exist but none is working, it requests outbox messages every fifth loop; delivery answers are checked each loop (`RemoteOutbox.cs:63–87`). | The topic should notify on new messages; an answer topic/change should notify on answer. The plan does not spell out that answer path (`RemoteOutbox.cs:76–83`, `implementation-plan.md:1763–1767`). |
| Follower reconnect backoff | Reconnect starts at one second and doubles to 30 seconds after a dropped or out-of-order stream; unreachable or call failure gives up until `Resume` or a new watch (`RemoteFollower.cs:96–137,154–157`). | Reopen the shared socket and restore its row subscriptions after connection readiness; the plan expects reconnect catch-up without duplicates (`implementation-plan.md:1777–1781`). |
| Follower settle | Current implementation waits until two seconds of quiet, then waits on a semaphore for a row or watcher event. It does not poll every 500 ms (`RemoteFollower.cs:22–24,243–264`). | Use the rows-topic subscription and its open-run/viewer state to decide whether to unsubscribe. **Assumed proposal.** |
| Plan’s stated 500 ms settle poll | The timer inventory describes a 500 ms settle poll, but the current `SettleAsync` uses a two-second quiet delay followed by an event wait (`implementation-plan.md:662–665`, `RemoteFollower.cs:247–264`). | Correct the plan inventory; no 500 ms poll is present in this file. |
| SSH keepalive | SSH sends a keepalive every 15 seconds and exits after three missed responses (`RemoteTunnel.cs:59–66`). | Keep this at the SSH transport layer; the SSH exit event can mark the machine unreachable (`RemoteTunnel.cs:44–45,156–163`). |
| SSH supervisor backoff | After SSH exits, the supervisor waits 1 second, doubles up to 30 seconds, and resets to 1 second after a tunnel lived longer than the max backoff (`RemoteTunnel.cs:126–184`). | This restarts the tunnel; socket reconnect should follow the tunnel becoming available. The plan’s backend-clock retry statement covers machine retries, not explicitly this SSH process supervisor (`implementation-plan.md:1774–1776`). |
| SSH child wait | The wrapper sleeps for one second while waiting for its SSH child; the wrapper also installs `SIGCHLD` handling (`RemoteTunnel.cs:47–53`). | This is tunnel process supervision, not a request poll; the child-exit event remains the useful signal. |
| Initial forward readiness | `WaitForPortAsync` retries every 100 ms until the forward accepts or the 15-second deadline expires (`RemoteTunnel.cs:107–123`). | Use the socket hello/welcome as readiness once the tunnel has started; the current port check is only an initial tunnel setup check. |
| Tunnel disposal | Disposal checks the saved PID every 100 ms, up to 20 times, before killing the process group (`RemoteTunnel.cs:208–222`). | No machine-link event replaces shutdown cleanup; this is process termination handling. |
| Machine status probe | `MachinesFeature` probes on the first backend-clock tick and every 15th tick while its topic is watched; each probe is limited to five seconds (`MachinesFeature.cs:19–22,139–180`). | Socket open/close plus the machine’s connection state can drive reachability. C2 says retries run on the backend clock (`implementation-plan.md:1774–1776`). |
| Explicit Connect retry | The saved-machines branch retries a failed HTTP probe every 500 ms for up to ten seconds (`origin/lane/machines:dotnet/Assembly.Application/Features/Chat/Sessions/MachinesFeature.cs:19–22,108–137`). | Wait for socket readiness/reconnect events instead of repeated HTTP version probes. **Assumed proposal.** |
| Adjacent message-status wait | `MessageStatusTool` checks status every 250 ms for up to 30 seconds; this is not the machine transport itself (`MessageStatusTool.cs:9–13,24–34`). | Wake on the stored message’s delivery or answer change; the plan lists this as a poll to replace with pushes (`implementation-plan.md:665–666`). |

## 5. Auth and reconnect behavior

- The remote token is read over SSH from the configured token file and retained in memory (`RemoteTunnel.cs:90–105`). `RemoteMachine` sends it as an HTTP bearer token (`RemoteMachine.cs:24–46`); `RemoteFollower` also uses a bearer header for `/live` (`RemoteFollower.cs:163–168`).
- `TokenGuard` accepts bearer auth for ordinary routes and allows a token query only for `/live`; the socket endpoint instead validates the token in its first `hello` frame (`TokenGuard.cs:8–13,48–55`, `SocketConnection.cs:143–167`).
- The SSH tunnel detects dead links through SSH keepalives or a failed forward; its supervisor restarts SSH with backoff (`RemoteTunnel.cs:12–13,59–66,126–184`). The desktop marks a tunnel exit unreachable (`DesktopBackend.cs:137–139`). `RemoteMachine` also marks transport errors unreachable (`RemoteMachine.cs:248–275`).
- **Current `origin/main` launch path:** when `--remote` is specified, `DesktopServe` calls `RemoteTunnel.ConnectAsync` before starting the desktop backend (`DesktopServe.cs:26–36`). In this path, reading the token uses `CancellationToken.None`, and setup waits for the forwarded port for up to 15 seconds; a remote that is down can prevent the local backend from reaching its ready line (`RemoteTunnel.cs:72–87,107–123`, `DesktopServe.cs:29–36`).
- **Saved-machines branch behavior:** startup queues one connection attempt for each saved profile; if opening it fails, the branch marks it unreachable and does not add it to `MachineSet` (`origin/lane/machines:dotnet/Assembly.Infrastructure/Remote/RemoteConnections.cs:50–60,104–121`). Its periodic machine probe iterates active remotes only (`origin/lane/machines:dotnet/Assembly.Application/Features/Chat/Sessions/MachinesFeature.cs:154–169`), so a profile that failed before becoming active is not retried by that probe. The explicit Connect call queues a fresh attempt (`origin/lane/machines:dotnet/Assembly.Infrastructure/Remote/RemoteConnections.cs:63–67`).
- C2 says startup from saved machines and opening while one is down must remain supported, with retries on the backend clock (`implementation-plan.md:1774–1776`). The branch’s one-shot failed startup path does not yet implement automatic retry for a profile that never became active (`origin/lane/machines:dotnet/Assembly.Infrastructure/Remote/RemoteConnections.cs:104–121`, `origin/lane/machines:dotnet/Assembly.Application/Features/Chat/Sessions/MachinesFeature.cs:154–169`).

## 6. Existing tests

| Test group | What it covers | Status |
|---|---|---|
| `RemoteMachineTests.cs` | Remote session creation, row streaming/copy, catch-up, restart recovery and format mismatch (`RemoteMachineTests.cs:43–145`). | Regular tests under `Infrastructure/Remote`, not Draft or Pinned. |
| `RemoteMachineTests.cs` helper tests | Cross-machine helper creation, outbox delivery and answers, late delivery, and remote task stop (`RemoteMachineTests.cs:249–357`). | Regular tests under `Infrastructure/Remote`, not Draft or Pinned. |
| `RemoteMachineTests.Restart.cs` | Follower reconnect and backoff (`RemoteMachineTests.Restart.cs:130–165`). | Regular tests under `Infrastructure/Remote`, not Draft or Pinned. |
| `RemoteFollowerTests.cs` | Quiet-link timers and settling when the last viewer unsubscribes (`RemoteFollowerTests.cs:39–95`). | Regular tests under `Infrastructure/Remote`, not Draft or Pinned. |
| `RemoteTunnelTests.cs` | Process group stop, restart after exit, exit notification and immediate retry (`RemoteTunnelTests.cs:11–64`). | Regular tests under `Infrastructure/Remote`, not Draft or Pinned. |
| `SessionSummariesTests.cs` | Startup summary loading and summary updates as rows are published (`SessionSummariesTests.cs:14–74`). | Regular tests under `Features/Chat/Rows`, not Draft or Pinned. |
| `Draft/Base/MachinesSocketTests.cs` and `.Connect.cs` | Machine topic state, reachability changes, probes, and explicit Connect retry behavior (`MachinesSocketTests.cs:92–185,225–257`, `MachinesSocketTests.Connect.cs:9–52`). | Draft; the Draft README says those tests wait for a corrected behavior list (`Draft/README.md:1–4`). |
| `Draft/Base/RemoteFollowerTests.cs` | Follower pass, give-up, and re-watch behavior (`RemoteFollowerTests.cs:46–112`). | Draft. |
| Saved-machine branch tests | Profile startup and its non-blocking startup behavior (`origin/lane/machines:dotnet/Assembly.Tests/Infrastructure/Remote/RemoteConnectionsTests.cs:308–342`). | Regular branch tests; saved-machine socket tests are in `Draft/Base`. |
| Board branch tests | Catch-up, remote calls, cancellation and reconnect (`origin/lane/board:dotnet/Assembly.Tests/Draft/Board/BoardRemoteTests.cs:82–165,187–225`). | Draft Board tests. |

I found no remote or machine test files under `dotnet/Assembly.Tests/Pinned`; pinned tests are held under `Pinned/` and changes to them require approval (`Pinned/README.md:1–3`). The search was limited to test paths matching Remote, Machine, Follower and Mirror.

## 7. Proposed implementation cut

These are proposed independent slices; estimates are **assumed**. Each test should be written and observed failing before implementation, as required for behavior changes (`AGENTS.md:170`). Do not add a new generic socket abstraction: C2 names `RemoteMachine` as the socket client (`implementation-plan.md:1763–1767`).

### 1. Rows on the shared live socket

- **Files:** `dotnet/Assembly.Infrastructure/Remote/RemoteMachine.cs`; `dotnet/Assembly.Infrastructure/Remote/RemoteFollower.cs`; `dotnet/Assembly.Remote/Serve/ServeRun.cs`; `dotnet/Assembly.Tests/Infrastructure/Remote/RemoteMachineTests.cs`; `dotnet/Assembly.Tests/Infrastructure/Remote/RemoteFollowerTests.cs`.
- **Failing test first:** `Remote_rows_socket_replays_after_and_copies_each_row_once_after_reconnect`.
- **Scope:** make `RemoteMachine` own the live machine socket and have `RemoteFollower` subscribe to `rows` with `after`; register `RowsFeature` on `Assembly.Remote` (`implementation-plan.md:1763–1771`, `RowsFeature.cs:51–68`, `ServeRun.cs:54–60`).
- **Delete in this slice:** the direct `/live` client connection in `RemoteFollower`; keep the server `LiveSocket` route until the frozen app and `check4` scenario move (`RemoteFollower.cs:163–171`, `implementation-plan.md:1447–1454`, `live-rows.ts:7–11`).
- **Estimated diff:** 200–300 changed lines (**assumed**).

### 2. Session, run, config and summary relay

- **Files:** `dotnet/Assembly.Infrastructure/Remote/RemoteMachine.cs`; `dotnet/Assembly.Remote/Serve/ServeRun.cs`; `dotnet/Assembly.Infrastructure/Socket/SocketFrames.cs`; `dotnet/Assembly.Application/Features/Chat/Sessions/SessionsFeature.cs`; `dotnet/Assembly.Application/Features/Chat/Sessions/SendFeature.cs`; `dotnet/Assembly.Application/Features/Chat/Sessions/ConfigFeature.cs`; `dotnet/Assembly.Application/Features/Chat/Sessions/SessionStore.cs`; `dotnet/Assembly.Application/Features/Chat/Rows/SessionSummaries.cs`; `dotnet/Assembly.Tests/Infrastructure/Remote/RemoteMachineTests.cs`.
- **Failing test first:** `Remote_session_topic_relays_remote_sessions_and_fresh_summaries`.
- **Scope:** migrate the version/agents/session/run/config calls, subscribe to the remote `sessions` topic, and verify summary changes are pushed by the remote host (`SessionsFeature.cs:33–50,77–89`, `SendFeature.cs:34–72`, `ConfigFeature.cs:28–43`, `SessionSummaries.cs:36–69`).
- **Version detail:** the replacement for `/version` is not named by C2; `welcome` currently lacks the row format version (`SocketFrames.cs:10–14`, `RemoteMachine.cs:208–220`). Resolve that contract before removing the compatibility check.
- **Delete in this slice:** the old `RemoteMachine` HTTP calls for version, agents, sessions, run control and config. Keep the shared server route definitions until the frozen app cut (`RemoteMachine.cs:64–86,107–128,150–173`, `implementation-plan.md:1447–1454`).
- **Estimated diff:** 350–500 changed lines (**assumed**).

### 3. Tool details and attachment transfer on bulk

- **Files:** `dotnet/Assembly.Infrastructure/Remote/RemoteMachine.cs`; `dotnet/Assembly.Remote/Serve/ServeRun.cs`; `dotnet/Assembly.Infrastructure/Chat/Attachments/AttachmentsFeature.cs`; `dotnet/Assembly.Application/Features/Chat/Rows/RowsFeature.cs`; `dotnet/Assembly.Tests/Infrastructure/Remote/RemoteMachineTests.cs`.
- **Failing test first:** `Remote_bulk_calls_send_tool_details_and_a_20_mb_attachment`.
- **Scope:** route `rows/tool` and chunked `attachments/*` over `/socket/bulk`; each frame remains under the 4 MB limit (`RowsFeature.cs:78–89`, `ChatSocketTests.Attachments.cs:12–31`, `SocketConnection.cs:20–22`).
- **Delete in this slice:** the `RemoteMachine` HTTP tool-detail and image-upload calls. Keep shared route definitions until the old page is cut (`RemoteMachine.cs:133–148`, `implementation-plan.md:1447–1454`).
- **Estimated diff:** 150–250 changed lines (**assumed**).

### 4. Outbox push and polling removal

- **Files:** `dotnet/Assembly.Infrastructure/Remote/RemoteOutbox.cs`; `dotnet/Assembly.Infrastructure/Remote/RemoteMachine.cs`; `dotnet/Assembly.Remote/Serve/ServeRun.cs`; `dotnet/Assembly.Application/Features/Chat/Sessions/SessionsFeature.cs`; `dotnet/Assembly.Application/Features/Chat/Sessions/SessionStore.cs`; `dotnet/Assembly.Infrastructure/Http/OutboxRoutes.cs`; `dotnet/Assembly.Tests/Infrastructure/Remote/RemoteMachineTests.cs`.
- **Failing test first:** `Remote_outbox_pushes_messages_and_answers_without_polling`.
- **Scope:** publish outbox changes to a topic, deliver acknowledgement and answer through socket calls, and remove the polling task (`RemoteOutbox.cs:41–83`, `OutboxRoutes.cs:18–23`).
- **Delete in this slice:** the desktop’s HTTP `GET /outbox` and POST acknowledgement/answer calls, then the now-unused `OutboxRoutes`. C2 does not specify the exact topic or call names, so those names remain **assumed** (`RemoteMachine.cs:89–105`, `implementation-plan.md:1763–1767`).
- **Estimated diff:** 200–300 changed lines (**assumed**).

## 8. Risks

- Deleting `LiveSocket` in C2 breaks the existing live-row check: `check4.sh` runs every scenario, and `scripts/features/live-rows.ts` opens `/live` (`check4.sh:8–12,42–48`, `live-rows.ts:7–11`).
- The route classes are shared: `ApiServer` maps chat routes and `/live` for either host, so deleting them for remote clients also removes endpoints used by the frozen page and current checks (`ApiServer.cs:39–53`, `implementation-plan.md:1447–1454`).
- `check4.sh` probes `/version`, creates sessions and sends turns over HTTP; moving callers and deleting shared routes must be sequenced with the scenarios and page cut (`check4.sh:68–78,109–116`).
- Workbox Test serves a published `Assembly.Remote` build through its systemd unit, and `check4` expects local and remote backends to report the checked commit (`workbox.sh:102–124`, `check4.sh:68–79`). Missing remote feature registration would therefore fail remote scenarios even if local checks pass (`ServeRun.cs:54–60`).
- If the Board branch lands unchanged, `RemoteWorkflowMirror` owns a separate `/socket` connection while C2’s `RemoteMachine` adds another live socket; that exceeds C2’s one-live/one-bulk pair unless Board shares the connection (`origin/lane/board:dotnet/Assembly.Infrastructure/Workflows/RemoteWorkflowMirror.cs:14–16,39–60`, `implementation-plan.md:1763–1771`).
- The saved-machine branch marks a failed initial connection unreachable but does not retry that inactive profile from its periodic active-remote probe; C2 requires backend-clock retries (`origin/lane/machines:dotnet/Assembly.Infrastructure/Remote/RemoteConnections.cs:104–121`, `origin/lane/machines:dotnet/Assembly.Application/Features/Chat/Sessions/MachinesFeature.cs:154–169`, `implementation-plan.md:1774–1776`).
- Removing `/version` without a replacement drops the current guard that refuses incompatible row formats; the existing integration test covers that refusal (`RemoteMachine.cs:208–220`, `RemoteMachineTests.cs:132–145`).

## Open questions for the owner

- When a saved machine is down at desktop launch, should it reconnect automatically on backend-clock ticks, or wait for explicit Connect? The saved-machines branch makes one startup attempt, while C2 says retries use the backend clock (`origin/lane/machines:dotnet/Assembly.Infrastructure/Remote/RemoteConnections.cs:50–60,104–121`, `implementation-plan.md:1774–1776`).
- Should the remote `sessions` topic include and push live summary fields, or only relay stored session metadata? C2 calls for relaying the sessions topic, while the summary note says remote summary freshness is a separate decision (`implementation-plan.md:1463–1478,1510–1514`).
- What socket contract replaces `/version`’s row-format check? The current welcome has protocol and machine but no format version, and the existing remote path refuses mismatched row formats (`SocketFrames.cs:10–14`, `RemoteMachine.cs:208–220`).
- Should C2 delete only the desktop’s old remote HTTP callers and defer shared route definitions plus `/live` until the app cut, as the frozen-page note says? The C2 deletion text and frozen-page sequencing disagree (`implementation-plan.md:1447–1454,1772–1773`).
- Does C2’s one-live-socket rule include the Board mirror added on the Board branch? The mirror owns its own socket, while C2 specifies one live and one bulk socket per remote (`origin/lane/board:dotnet/Assembly.Infrastructure/Workflows/RemoteWorkflowMirror.cs:14–16,39–60`, `implementation-plan.md:1763–1771`).

