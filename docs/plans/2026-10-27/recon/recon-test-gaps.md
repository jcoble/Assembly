Recon, read-only, 2026-10-27, not re-verified unless marked

# Recon: socket calls vs tests (base-cut, worktree HEAD 60ce21e; task said e8e361e - assumed same tree)
Paths: src = dotnet/Assembly.Application|Infrastructure; T = dotnet/Assembly.Tests. All receipts verified by grep/read unless marked assumed.
Draft/ = T/Draft/Base. Pinned/ holds only README.md (nothing pinned yet). Generic socket tests live in T/Infrastructure/Socket/SocketTests*.cs (fake "notes" calls, not real features).

## 1. Calls and their socket tests
Generic error paths (SocketDispatcher.cs:116,121,157,167,197,205,228-245,270), proven only with fake calls:
 not_found SocketTests.cs:59,69 | bad_args :73-82 | failed :305,331 | cancelled :126,235 | timeout :268-293,338-350 | over-size answer :300-306, error over 4MB :313-317, state over 4MB SocketTests.Topics.cs:97.
 unreachable: no generic test; only via real features (below). No test of "ordering" at dispatcher level besides the real-feature Order tests.

| call (src file:line) | socket tests (T file:line, name) | error paths covered |
|---|---|---|
| rows topic `calls.Rows` RowsFeature.cs:48 | RowsSocketTests.cs:76,89,104,125,140,161,179,191,229,282 (first open, resume, catch-up page, after>last, dedupe, byte budget, other session, needs a session, unsub, remote watch) | bad_args :191-196; ordering :89,:140; byte budget :161; unsub :229. Not: cancelled/timeout/failed/unreachable |
| rows/page RowsFeature.cs:63 | RowsSocketTests.cs:200,216; ChatSocketTests.Rows.cs:10 | clamp/at-most-a-page :216. No bad_args, not_found, failed(unknown session) |
| rows/tool RowsFeature.cs:71 | ChatSocketTests.Rows.cs:24,43,63,78; Order.cs:74 | cancel :63, socket close :78, big output cut :43 (cut, not the 4 MB limit). No unknown-session/unknown-item failed, no unreachable |
| sessions topic SessionsFeature.cs:31 | ChatSocketTests.Sessions.cs:9,36,111 | none (no error) |
| sessions/page SessionsFeature.cs:35 | Sessions.cs:49,132 (+:105,:60) | ordering of shared times :132. No bad_args |
| sessions/create SessionsFeature.cs:43 | Sessions.cs:70 (before answer), :85 unreachable; Args.cs:19 (empty agent/repoUrl); Order.cs:59 | unreachable :85-92; failed :19-30. NOT tested: "unknown machine", "no agent X", helper create (ParentSessionId), cancel/timeout |
| machines/list MachinesFeature.cs:20 | Config.cs:59 | - |
| machines/agents MachinesFeature.cs:22 | Config.cs:59,77,91; Args.cs:35 | cancel :77, close :91, unknown machine failed :72 (checks via line 72; code assert assumed in 59-75) |
| config/get ConfigFeature.cs:30 | Config.cs:8,29,44 | failed unknown session :24-25; cancel :29; close :44 |
| config/set ConfigFeature.cs:35 | Config.cs:8; Args.cs:19 | failed empty configId Args.cs:24-25. No unknown-session, no cancel, no unreachable |
| send/send SendFeature.cs:36 | Send.cs:10,23,41; Order.cs:15,39,74; Attachments.cs:183 | failed ×3 (:30-34), unreachable :41-49, ordering Order.cs:39, hung machine Order.cs:15 |
| send/stop SendFeature.cs:46 | Send.cs:53; Order.cs:15 | failed unknown run :65-66. No unreachable |
| send/answer SendFeature.cs:52 | Send.cs:70; Args.cs:7 | failed non-object :12-14, not-open :80. No unknown session |
| send/stopTask SendFeature.cs:61 | Send.cs:85 | failed :95. Helper-on-other-machine branch (:66 area of SendFeature.cs) untested |
| attachments/begin AttachmentsFeature.cs:55 | Attachments.cs:13,35,163 | failed (type/size 0/oversize/unknown session) :35-46; 9th upload :163 |
| attachments/chunk :85 | Attachments.cs:13,50,183,208 | failed out-of-place/too big :50; cancelled :208-227; ordering :183 |
| attachments/commit :108 | Attachments.cs:13,76,98 | failed wrong bytes :85; owner socket :98 |
| attachments/drop :127 | Attachments.cs:142,163,183 | unknown id answers ok :157 |
| attachments/read :132 | Attachments.cs:252 | failed path traversal :267; piece size (1 MB pieces) :259-262. No cancel, no missing file |
| preferences topic + save (PreferencesFeature.cs:26,28) | PreferencesSocketTests.cs:41,58,73; SocketTests.Saves.cs (generic: failed :73,:124-125, bad_args :123) | bad_args/failed only generically; none on the real preferences/set with a bad value |
| clock topic BackendClock.cs:32 | ClockSocketTests.cs:66,80 | none |
| diagnostics memory.sample DiagnosticsFeature.cs:24 | DiagnosticsSocketTests.cs:58 | - |
| activity.begin/end :25,:26 | DiagnosticsSocketTests.cs:66 | - |
| diagnostics drop :27 | DiagnosticsSocketTests.cs:75 | - |
| soak.append :28 | DiagnosticsSocketTests.cs:82 | failed ×5 :85-89 |

Error-code summary on REAL calls: not_found only for fake "nothing/here" (RowsSocketTests.cs:244-247) and generic; timeout never on a real call (only fake, SocketTests.cs:268,338); over-size 4 MB never on a real call (rows/page, rows/tool, attachments/read, sessions/page); unreachable only send/send and sessions/create; cancelled on rows/tool, config/get, machines/agents, attachments/chunk.

## 2. base#N -> test
(docs/behavior/base.md lists #1-#10 only; #11 is only in implementation-plan.md:1862 and "added in phase C2" - not in the file yet.)
- base#1 project = folder/worktree: no test (grep "worktree" finds no test).
- base#2 connect saved machines on launch, offline shown: no tagged test. Nearest untagged: RemoteMachineTests.cs, ChatSocketTests.Sessions.cs:85 (unreachable). No test of launch-time connect.
- base#3 add/remove machines while running: no test.
- base#4 layout/drawer/dock/dividers: no test (frontend; plan F1).
- base#5 tab row remembers tabs: no test.
- base#6 look/theme: no test.
- base#7 settings saved as they change, survive quit: Pins: base#7 at PreferencesSocketTests.cs:39 (A_theme_change_is_written_when_made_and_is_there_after_a_restart :41), PreferencesRoutesTests.cs:38 (Saved_values_come_back_after_a_restart :40), :55 (A_later_save_replaces_only_the_keys_it_names :57). Gap: nothing for "quits right away" with keystroke saves in flight (plan says still has to be shown in the window, plan:1861).
- base#8 session list shows new sessions without restart: untagged; ChatSocketTests.Sessions.cs:9 (sessions topic starts with page then any session that changes), :70 (create sends session before answer).
- base#9 Mode/Model menus and agent name same on remote as on Mac: MISTAGGED. ClockSocketTests.cs:64 says "Pins: base#9" but tests the clock (180 changes in three minutes). Nearest real: ChatSocketTests.Config.cs:8 (config get/set from machine), :59 (machines/agents). Effectively no test pinning base#9.
- base#10 stale load stopped in page and backend: tagged FeatureCallsTests.cs:45 (A_call_the_page_aborts_stops_its_work_in_the_backend :47, HTTP abort) and SocketTests.cs:114 (Cancel_fires_the_handlers_token :115). Same-key replacement aborting the older sub/call (plan:486 "same key aborts the older") has no test I found (grep SocketTests.Keyed.cs not read in detail - assumed it covers it).
- base#11: no test (and no line in base.md yet).

## 3. Likely-bug gaps, most suspicious first
1. base#9 pinned by the wrong test; Mode/Model parity local vs remote has no test - ClockSocketTests.cs:64.
2. sessions/create "unknown machine" / "no agent X" / helper-create branches untested - SessionsFeature.cs:46-53.
3. No real call tested for timeout or the 4 MB over-size: rows/page, rows/tool, attachments/read, sessions/page - SocketDispatcher.cs:236,270 only exercised by fakes (SocketTests.cs:268,300).
4. unreachable only on send/send and sessions/create; never rows/tool, rows/page, config/get/set, machines/agents, send/stop/answer/stopTask, attachments (machine-bound) - SendFeature.cs:46-65, ConfigFeature.cs:30-37.
5. rows/page and rows/tool have no failed (unknown session / item) test - RowsFeature.cs:63-71.
6. send/stopTask helper-on-another-machine branch untested - SendFeature.cs:61-70.
7. preferences/set real-call bad_args / bad value untested (only generic Saves) - PreferencesFeature.cs:28.
8. config/set unknown session and cancel untested - ConfigFeature.cs:35.
9. base#1-#6, #11: no tests (base#2 launch-connect to saved machines, #3 add/remove machine at runtime are backend-testable: RemoteMachineTests.cs only).
10. Pinned/ is empty, so nothing is locked yet - T/Pinned/README.md.
