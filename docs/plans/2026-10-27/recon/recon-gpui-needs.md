Recon, read-only, 2026-10-27, not re-verified unless marked

# GPUI spike vs backend main (e8e361e / base-cut 60ce21e): what the session view needs

Spike: /home/blackcolours/dev/work/assembly-gpui-spike/app/src (S). TanstackAISpike is empty. Backend root: /home/blackcolours/dev/work/worktrees/Assembly/base-cut/dotnet (B).
All receipts verified by reading source unless marked assumed. Nothing run.

## 1. Headline
The spike does NOT speak the /socket protocol at all. It uses the old HTTP + /live surface (ureq GET/POST + tungstenite on /live).
Main still serves all of those routes (the plan keeps them until the cut, plan lines ~1441, 1464, 1515-1543). So the spike runs today
against main's HTTP, but moving it to /socket means rewriting S backend/{client,live,wire}.rs (about 320 lines). The row payload
types in wire.rs (Block, Inline, payloads) carry over unchanged.

## 2. Spike's requests and the match on main

| # | Spike (file:line) | Wire | Main today | On /socket (calls/topics) | Mismatch |
|---|---|---|---|---|---|
| a | client.rs:209-211 sessions(), wire.rs:10 | GET /sessions?limit=200, Bearer header (client.rs:238-240) | B Http/SessionRoutes.cs:21,26-43 (limit clamp 200, :28) | topic `sessions` (B Features/Chat/Sessions/SessionsFeature.cs:31) state = newest 200; call `sessions/page` (:35) | none on fields (see below) |
| b | wire.rs:49-63 SessionPage/SessionInfo: `sessions` (alias `threads`), `sessionId`, `agent`, `title?`, `machine?` | JSON | SessionRoutes.Write :96-108 sends sessionId, agent, repoUrl, title, machine, parentSessionId, acpSessionId, updatedAt; page sends `nextBefore` (:37-40) | SessionItem(SessionId, Agent, RepoUrl, Title, Machine, ParentSessionId, AcpSessionId, UpdatedAt) Sessions/SessionItem.cs:6-7; camelCase (SessionsJson.cs:6) | spike ignores extra fields (serde default). Socket `state` frame wraps each as `{key,item}` (Socket/SocketDispatcher.cs:272, 304), so the parser is new. No field the spike reads is missing. |
| c | client.rs:213-215 rows(), wire.rs:14-19: GET /sessions/{id}/rows?before=&limit= (PAGE const, panel.rs:82,158) | RowPage{rows, hasBefore} wire.rs:65-70 | B Http/RowRoutes.cs:19,23-46 sends rows, hasBefore, before, activeRunId | call `rows/page` args {session, before=long.MaxValue, limit<=500} (B Features/Chat/Rows/RowsFeature.cs:63, :124; result built :101-120 identical keys) | none. Spike drops `before` and `activeRunId` (unused). Arg name is `session` not `id`. |
| d | wire.rs:72-82 Row: seq, itemId?, kind, payload, firstSeq?, prevSeq? | | RowWire.Write sends sessionId, seq, runId, itemId, kind, payload, createdAt, +firstSeq (page rows) +prevSeq (live rows) (B Rows/RowWire.cs:36-48) | same JSON inside `rows` frame (SocketDispatcher.cs:356-366) | none. runId/createdAt/sessionId unread. |
| e | live.rs:40-71 + wire.rs:30-33: WS /live?session=&after=&token= ; frame `{type:"row",row}` ; ping frame has no row (live.rs:301-303) | | B Http/ApiServer.cs:46-51, Http/LiveSocket.cs:12-14,105-113 (`{type:"row",row}`, ping every 30 s); token in query only for /live (Http/TokenGuard.cs:52-53) | topic `rows` with `sub{key=session, after}`; frames `{t:"rows",sub,row}` (SocketDispatcher.cs:356-366) after a `state{sub,seq,items:[]}` (:323-330). `prevSeq` gap rule still holds (plan 5.1 table, line ~401) | Frame envelope differs (`t:"rows"` vs `type:"row"`), token goes in the `hello` frame, not the URL. The spike's gap check (live.rs:308 `prev_seq != watermark`) can stay. Resume: backend catches up at most one 500-row page / 8 MB, else starts at last seq (RowsFeature.cs:19-26, :48-54); the window must then call rows/page for history (spike already does). |
| f | client.rs:221-233 send(): POST /sessions/{id}/send body `{text}` (wire.rs:21-23,35-37), reply `{runId}` (wire.rs:91-95) | | B Http/RunRoutes.cs:23 | call `send/send` args {session, text} -> {runId} (B Sessions/SendFeature.cs:36-45 [file line = shown line - 67], records at :77,:79) | none on shape. Plan 5.10: a dropped send is never retried. |
| g | client.rs:217-219 tool_detail(): GET /sessions/{id}/tools/{item} -> {output?, diff?} (wire.rs:25-27, 97-101); used by changes/diff_panel.rs:31 | | B Http/RowRoutes.cs:20,49-79 (summary,input,output,diff,path) | call `rows/tool` args {session, item} (RowsFeature.cs:71-82, :126) -> ToolDetail adds `truncated`, `outputBytes`, `diffBytes` (:132-133); output and diff cut to 1 MB | none (extra fields ignored). Note `truncated`. |
| h | stop | spike has NO stop. Only reads turn state: model.rs:179-183, 262-264 (`is_running` = turn "started"). grep for stop/cancel in S finds nothing but live.rs's own flag | POST /runs/{id}/stop RunRoutes.cs:24 | call `send/stop` args {runId} -> {ok} (SendFeature.cs:46-51) | Spike needs the runId: send() returns it (client.rs:232) but panel.rs does not keep it (assumed, not read in full). On reopen the runId comes from `rows/page` result `activeRunId` (RowsFeature.cs:116) which the spike drops (wire.rs:65-70). |
| i | Row payloads: user{text}, delta{from,blocks}, message{blocks}, thinking{text,done}, tool{name,state,summary?,path?,added?,removed?}, turn{state}, error{message} (wire.rs:103-146); kinds plan/stats/context ignored (model.rs:186) | | Domain ToolPayload(Name, State, Summary, Path, Added, Removed, HasDetail,...) B Assembly.Domain/Rows/ToolPayload.cs:9 ; TurnPayload(State, Reason?) Domain/Rows/TurnPayload.cs:7 | same | none. Spike's own test uses `hasDetail` (model.rs:346) but wire.rs doesn't declare it; harmless. Other payload shapes (delta/message/thinking/user/error) assumed from the Svelte-port comment, not re-checked against RowJsonContext. |
| j | config: ASSEMBLY_URL default http://127.0.0.1:18990, ASSEMBLY_TOKEN_FILE `KEY=TOKEN` line (config.rs:15-22) | | token = same bearer | /socket needs `hello{token,protocol}` first (plan 5.1); TokenGuard skips /socket (TokenGuard.cs:39) | spike never sends `hello`. |

Spike does not call: machines, agents, sessions/create, answer, stopTask, config get/set, attachments.

## 3. What the session view needs that is not on main

Session list (S sessions/panel.rs:90-99 shows title, agent, machine only):
- On main SessionItem has no state, activeRunId, pendingAsk, background, needsAttention, unread (SessionItem.cs:6-7). PR #24 adds them plus `sessions/read` (assumed from your brief, not in this worktree). A useful list needs state (running dot) and unread/needsAttention.
- Machines: `machines/list` returns only remote names, no reachable/reason (MachinesFeature.cs:20-21, :32). The M1/M2 machines topic with reachable+reason is not on main. The spike shows `machine` text only (panel.rs:92) so it can run without it, but an unreachable machine's sessions would show nothing.

One session's rows:
- All present: `rows` topic + `rows/page` + `rows/tool` (RowsFeature.cs:44-83). Nothing missing for reading.
- A running indicator today comes from the newest `turn` row (model.rs:262), which works with main as is. `activeRunId` in `rows/page` (RowsFeature.cs:116) exists on main for stop.

Composer, send, stop:
- `send/send` and `send/stop` exist (SendFeature.cs:36,46). Missing on main: nothing for the basic case. `send/answer` (:52) exists for asks, but the spike has no ask card, and the list needs `pendingAsk` from #24 to know when one is open.
- Attachments exist as chunked calls (Infrastructure/Chat/Attachments/AttachmentsFeature.cs:55-132), not used by the spike.

Window-side gap that is not backend work: a Rust /socket client (hello, call/result/error, sub/state/change/rows, unsub, cancel), which the spike lacks entirely.

## 4. Ordered build list (backend first, smallest steps)
1. Nothing blocks the spike reading sessions and rows today: it can keep using HTTP /sessions, /rows and /live on main until the cut. Keep those routes until a Rust /socket client exists (plan: routes go with their last caller).
2. Merge PR #24 (SessionItem state/activeRunId/pendingAsk/background/needsAttention/unread, `sessions/read`) so the list can show running/unread/asks.
3. Merge M1/M2 machines topic (reachable, reason) so the list can show an offline machine.
4. Spike side, not backend: write the /socket client in Rust (hello/welcome, call, sub on `sessions` and `rows` with `after`, parse `{key,item}` state items and `rows` frames), then switch list, rows, send, stop, tool detail to the calls in the table above.
5. Spike side: keep `activeRunId` from `rows/page` and from `send/send`'s `Sent.runId` for `send/stop`.
6. Only after 4-5, the cut can delete /live and the HTTP chat routes (plan ~1464, 1515-1543); note RemoteFollower still uses /live (plan ~1489, 1543) so /live stays until C2.

Assumed, not verified: PR #24 and machines-work contents (taken from the brief); spike's panel.rs handling of the send reply runId; payload shapes for delta/message/thinking/user/error (not re-read in B).
