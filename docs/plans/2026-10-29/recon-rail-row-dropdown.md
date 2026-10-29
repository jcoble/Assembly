# Rail row menu and drop-down recon

HEAD: `fafb665e76fd17a706917f3948247cb08eea5310` (`git rev-parse HEAD`). Repository line references are for this commit.
Task sources: [TSK-1633](https://app.notion.com/p/408394b0689d818faf56cf8f2e622af0) and [TSK-1634](https://app.notion.com/p/408394b0689d81708439c94220ed620f).
The Assembly checkout's `app/` is only the spike; the richer legacy source is in `/home/blackcolours/dev/work/mac-command-bar` and is mapped below at its pinned commit.
The run-card note was read at commit `bc7ca66a` from `docs/plans/2026-10-29/run-card-recordings-frame-by-frame.md`.
An untracked copy is now present and its SHA256 matches that commit's blob: `e8dd836b7cf8f546501033545cf80b4295e3695651cffc32e2baefed97566f49` (`sha256sum` of both paths). I did not edit it.

## 1. Old app at the pinned source commit

**Old-app HEAD:** `d5f5fe68cfe395f6cc7aef0ef6ea57d94147559f` (`git -C /home/blackcolours/dev/work/mac-command-bar rev-parse HEAD`). The named rail files were clean at this checkout; unrelated old-app changes were left untouched. All line references in this section are for that hash.

### 1.1 Rail row and its data

- `tauri-svelte-preview/src/lib/shell/components/SessionRail.svelte` imports `WorktreeAgentRow`, `SessionRowContextMenu`, the row-menu builder and jump helper (`SessionRail.svelte:15-32`). It receives `sessions`, view options, saved `manualOrder`, selected id, and callbacks; it reads `rail.owned` for status counts and `rail.remoteProfiles` for group machine labels (`SessionRail.svelte:34-75,281-285`).
- The page passes selected sessions and actions down through `SessionsColumn` to `SessionRail` (`tauri-svelte-preview/src/routes/+page.svelte:621-641`; `SessionsColumn.svelte:439-456`). The click selects the session and calls `onSelectSession`; it does not open a row detail panel (`SessionRail.svelte:125-133`; `SessionRowVisual.svelte:13-22`).
- Group headings, not rows, are expandable. They show status, project, workflow-run, `Pinned`, or `Main orchestrator` groups and their counts; `Settled` starts collapsed (`SessionRail.svelte:116-123,243-321`; `myWorkViewOptions.ts:195-237`). Helpers may appear as indented child rows under a session, and workflow workers are grouped by run (`SessionRail.svelte:307-317`; `myWorkViewOptions.ts:201-220`).
- `WorktreeAgentRow.svelte` reads the `OwnedSession` passed to it: title/id, provider, cwd/project, branch, handle, lifecycle dates, pin and orchestrator stamps, helper/run fields, background work, presence/error and remote machine/profile (`WorktreeAgentRow.svelte:41-70,72-149,157-164,243-305`; `ownedSessions.ts:55-116`). It also reads `$sessionPresenceHistory[ownedId]` for turn state (`WorktreeAgentRow.svelte:76-107`; `conversation/sessionPresence.ts:8-13,52`).
- It renders the provider tile, title, age, machine, optional handle and branch, plus status counts or the short error summary (`WorktreeAgentRow.svelte:214-234,236-305`). The title comes from `sessionLabel`; its age starts at `startedAtMs` or falls back to parsed `lastActivity` (`WorktreeAgentRow.svelte:72,157-164`).
- Presence values are `Working`, `Waiting on you`, `Idle`, `Finished`, and `Error` (`WorktreeAgentRow.svelte:110-134`). A waiting marker counts this session plus waiting helpers and workflow workers; background tasks, helpers and active runs have separate tooltip counts (`ownedSessions.ts:172-227`; `WorktreeAgentRow.svelte:279-305`). A crown has the exact accessible and tooltip label `Main orchestrator` (`WorktreeAgentRow.svelte:243-255`).
- `SessionRail` has one shared 15-second age refresh, skipped while the document is hidden (`SessionRail.svelte:79-86`). `railElapsedTicker.ts:1-17` formats elapsed time as seconds below a minute, whole minutes below an hour, whole hours below a day, then whole days.
- The in-memory projection in `sessionRailStore.svelte.ts` has `owned`, `available`, `activeOwnedId`, `scanning`, `error`, `remoteConnections`, `remoteProfiles`, `scanningMachines`, and `runningElsewhere`; it does no I/O (`sessionRailStore.svelte.ts:1-53`). Its setters hydrate, replace, add, update, remove, and select rows (`sessionRailStore.svelte.ts:55-127`). No socket topic is read by these row components. Tauri conversation events patch `rail.owned`, and `remote-connection-changed` updates the remote state (`conversationService.ts:1019-1029,1269-1318`).

### 1.2 Row visual, Connect, and hover

- `SessionRowVisual.svelte` is only the selected-row button around its child content. It has `active` and click-selection states, not an expansion state (`SessionRowVisual.svelte:4-22`). The button is at least 48 px high, has a 32 px column, 12 px gap, `6px` radius and `6px 8px` padding. Hover uses `--accent`; selected uses `--secondary` and a 2 px `--primary` stripe (`SessionRowVisual.svelte:24-65`).
- `WorktreeAgentRow.svelte` draws a 32 px provider tile with an 8 px radius, two fixed text lines at 20 px and 16 px, 12 px secondary text, and theme variables for state colors (`WorktreeAgentRow.svelte:384-417,509-546,586-659`). The row has 8 px horizontal inset. Its reduced-motion rule only fades the age and tile for 120 ms; the row background has no transition (`WorktreeAgentRow.svelte:384-398,652-659`).
- Its CSS uses `--muted` and `--muted-foreground` for the provider tile, `--secondary` when selected, `--color-text-3` for age, `--color-bad` for errors, `--foreground` for the title, and `--primary` for a working title or crown (`WorktreeAgentRow.svelte:403-417,586-590,597-630,636-659`).
- The status badge tooltip copy is `Needs you: <count> (<items>)`, `<count> background task(s) running`, `<count> helper session or sub-agent(s) running`, `<count> workflow run(s) working`, and `Unread: the last turn finished and you have not read it` (`WorktreeAgentRow.svelte:279-300`).
- The rail scroll area fills its parent and keeps a stable thin scrollbar gutter; its custom scrollbar is 9 px wide (`SessionRail.svelte:359-382`).
- Group headings are sticky, at least 28 px high, with `8px 16px 4px` padding, 6 px gaps, 12 px uppercase text and `--card` / `--muted-foreground` colors; project headings use `--foreground` and normal case (`SessionRail.svelte:384-423`).
- Open headings rotate the chevron 90 degrees. With reduced motion allowed, that transition is 160 ms; the heading color transition is 120 ms, and rows enter over 160 ms with a 4 px upward offset (`SessionRail.svelte:425-443,509-533`).
- The needs count uses a 24 px minimum width, 20 px height, small radius, and attention foreground/background variables (`SessionRail.svelte:450-474`). Helper rows indent by `--space-5` and use a 1 px border from `--color-border` (`SessionRail.svelte:494-507`).
- A remote row with state `disconnected` shows `Connect`; state `reconnecting` shows disabled `Connecting…`; connected rows show neither button (`WorktreeAgentRow.svelte:137-149,324-336`). The page selects that session, checks it is still selected, then runs the same remote connection and refresh used by the conversation pane (`+page.svelte:481-495`). Connection state comes from the Tauri `remote-connection-changed` event (`conversationService.ts:1019-1029`).
- There is no hover flyout, ellipsis button, or hover `Archive` action in the row markup. The component comment mentions quick-jump buttons at hover, but the markup has none (`WorktreeAgentRow.svelte:237-241,268-308`; `rg -n "quick.jump|flyout|hover.*(menu|button|action)|onmouseenter|onmouseleave|popover" /home/blackcolours/dev/work/mac-command-bar/tauri-svelte-preview/src/lib/shell/components/WorktreeAgentRow.svelte /home/blackcolours/dev/work/mac-command-bar/tauri-svelte-preview/src/lib/shell/components/SessionRail.svelte` matched only that comment). The actual hover effect is the row background. Status and crown tooltips are controlled by `Tooltip.Root`, appear above the icon with a 6 px offset, and have no component-set delay (`WorktreeAgentRow.svelte:80-86,185-198,243-255`).

### 1.3 Menu and jumps

- Right-click opens `SessionRowContextMenu` at the pointer (`SessionRail.svelte:135-139,345-356`). It positions within the window and focuses the menu; outside pointer-down or `Escape` closes it (`SessionRowContextMenu.svelte:31-52`). The menu is 224 px wide, with 4 px padding, 4 px radius and `--menu-surface`; items are at least 32 px high. Hover/focus uses a 10% text-color mix; disabled and destructive items use `--color-disabled-text` and `--color-bad` (`SessionRowContextMenu.svelte:87-91`).
- Exact menu copy, in order: `Rename`; `Move to project…`; `Pin to top` or `Unpin`; `Make main orchestrator` or `Unmark main orchestrator`; `Mark done`, `Move back to Working`, or `Move back to Done`; `Archive (Settle)` when not settled; `Copy deeplink`; `Copy session id`; `Copy resume command`; `Copy worktree path`; disabled `Continue in new session`; `Open in editor`; `Open source control`; disabled `Reveal in Finder`; destructive `Delete` (`sessionRowMenu.ts:66-153`). The orchestrator action is disabled for helpers and workflow workers with `Only a top-level session can be the main orchestrator` (`sessionRowMenu.ts:73-88`). Missing copy values explain why they are disabled; unavailable Continue and Reveal say `Not available yet` (`sessionRowMenu.ts:104-150`).
- `Move to project…` opens a submenu with `Back` and project names, hydrates projects, then calls `moveSessionProject(ownedId, projectId)` (`SessionRowContextMenu.svelte:19-29,63-81`). `Rename` opens the row input; Enter or blur saves a changed nonempty name, Escape cancels (`SessionRail.svelte:201-205,234-237`; `WorktreeAgentRow.svelte:166-182,310-322`).
- `Mark done`, reopen, archive and unarchive call the lifecycle callbacks; Pin/Unpin calls `onPin`; orchestrator calls `onMarkOrchestrator`; Delete calls `onAskRemove` (`SessionRail.svelte:201-220`). The page saves pin and title in session metadata with `update_agent_conversation_session_meta`; record projection restores `pinnedAt` (`+page.svelte:175-212,636-640`; `ownedSessions.ts:434-437,455-470`; `tauriSource.ts:2611-2621`).
- Main orchestrator is stored separately from session metadata, on the owning machine, in `app_settings` under `orchestrator.main`; the setting's `updated_at` is its mark time (`core/src/broker.rs:155,260-275`; `set_main_orchestrator` Tauri wrapper at `tauriSource.ts:2241-2245`). Session listing reads that setting and projects its timestamp onto the matching row, which the Svelte model restores (`agent_conversation/manager.rs:3619-3624,3672-3674`; `ownedSessions.ts:434-438`).
- Copy actions call `navigator.clipboard.writeText`: `assembly://session/<ownedId>`, native session id or owned id, provider resume command, and cwd (`SessionRail.svelte:196-199,213-217`). Editor and source-control actions call `sessionRowJump`; it selects the session first, then opens the center `session` or `editor` panel, or the `source-control` sidebar (`SessionRail.svelte:130-133,218-219`; `sessionRowJump.ts:17-34,49-62,83-90`).

### 1.4 Saved groups and custom drag order

- Pin is a `pinnedAt` session metadata timestamp. The newest marked top-level row becomes the separate `Main orchestrator` section; pinned rows form the `Pinned` section below it (`ownedSessions.ts:96-103`; `myWorkViewOptions.ts:223-237`). These are not saved as rail-group records.
- Custom order is a local Assembly setting, not a session-table field. `SessionsColumn.svelte` restores `rail.session-order` with `readAssemblySettingFromTauri` and writes drops with `writeAssemblySettingFromTauri`; the write uses `write_assembly_setting`, the read uses `read_assembly_setting` (`SessionsColumn.svelte:113-127,179-203`; `tauriSource.ts:2726-2771`). A drop switches sort to `manual` (`SessionsColumn.svelte:190-200`).
- Drag handlers live once on the list. They record the row id and group, allow a drop only in that same group, choose before/after from the target midpoint, and call `reorderMyWorkSessions`; helper rows have no group key and are not drag targets (`SessionRail.svelte:141-194,307-319,325-333`). The helper rewrites only the dragged group’s places and preserves other ids (`myWorkViewOptions.ts:272-295`).
- `Pinned` rows are sorted by the active sort. `Main orchestrator` is lifted above them, and status/project groups follow (`myWorkViewOptions.ts:223-237`).

### 1.5 The requested row detail is not present in the old rail

- The row does not expand in place. Its row click selects; only group headings collapse. The exact per-row tabs `Needs me`, `Workflow`, `Sub-agents`, and `PRs`/`Changes`, plus an inline `Send` field, were not found in the old rail (`SessionRowVisual.svelte:13-22`; `SessionRail.svelte:116-133,253-321`; search: `rg -n -i "needs me|needs-me|workflow step|sub-agent|opened row|row dropdown|session detail|answer question|inline answer" /home/blackcolours/dev/work/mac-command-bar/tauri-svelte-preview/src/lib/shell/components /home/blackcolours/dev/work/mac-command-bar/tauri-svelte-preview/src/lib/shell/ownedSessions.ts /home/blackcolours/dev/work/mac-command-bar/tauri-svelte-preview/src/lib/shell --glob '!**/git/**' --glob '!**/node_modules/**'`).
- **Questions live in the Board, not the row.** `BoardButton` counts unanswered owner questions and waiting workflow items across machines, reads while open, and recounts on `agent-messages-changed` or a remote connection change (`board/BoardButton.svelte:17-64,67-85`). `boardSource.ts` reads `board`/`board_count`; `MachineBoard.questions` carries question ids, title/body/recommended text, owning session/machine, answer and timestamps (`board/boardSource.ts:25-58,73-77`). `BoardQuestions.svelte` shows an inline `Type an answer…` field, max 600 characters, with Enter to send; there is no button named `Send`. Its buttons are `As recommended`, `No`, `Explain more`, `Dismiss`, and `Open` (`board/BoardQuestions.svelte:67-93,111-158`). Answering sends `Q<n>: <answer>` to the session, then records the answer on the owning machine (`ownerInboxActions.ts:60-107`).
- The Board badge is the count of waiting session rows plus the combined Board question/workflow count; it caps the displayed number at `99+` (`board/BoardButton.svelte:18-23,67-80`; `board/boardSource.ts:55-57`).
- A Board question record carries `id`, `key`, title, body, recommended text, owning session id/name/state, answer and asked/answered timestamps; it is nested under its machine in the Board read (`board/boardSource.ts:25-39,50-57`).
- **Workflow waits live in Agents > Inbox, not the row.** `OwnerInbox.svelte` gets `OwnerInboxStep[]` from `list_owner_inbox`, reloads on `agent-messages-changed` while visible and reloads after an action (`OwnerInbox.svelte:39-68,74-105`; `tauriSource.ts:2307-2323`). It shows `Approval`/`Answer`, reason and `Approve`, `Another round`, `Accept`, `Stop`, and `Open` actions (`OwnerInbox.svelte:119-168`). It has no per-step inline answer field. The broader Board read also has `BoardRun` and `BoardStep` state (`boardSource.ts:14-23,30-39`).
- Workflow actions call the inbox decision path for the selected step and refresh the list after the result; opening the owner session is a separate action (`OwnerInbox.svelte:74-105,119-168`).
- **Sub-agents have two existing presentations, neither an opened rail row.** The rail draws separate `OwnedSession.helperOf` rows and workflow-run worker groups (`myWorkViewOptions.ts:201-220,240-249`). The Agents panel derives a list from `conversation.children`, with status and recent activity (`AgentsPanel.svelte:115-129,376-394`; `agentActivityModel.ts:19-52,114-140`). `childUpdate` and `children.updated` conversation events merge into that child list (`conversationStore.svelte.ts:690-697`); `childUpdate` also updates the rail’s helper count (`conversationService.ts:1270-1286`).
- **Pull requests live in GitHub’s separate workspace.** `PullRequestWorkspace.svelte` lists repository PRs through `list_github_pull_requests` and reads a selected PR through `read_github_pull_request`; rail rows only carry optional `pullRequest` metadata used to associate a session to a selected PR (`PullRequestWorkspace.svelte:122-145,205-238,254-287`; `ownedSessions.ts:108-116`; `tauriSource.ts:1838-1854`). The list updates through Refresh, search, filter or project selection (`PullRequestWorkspace.svelte:763-779`). No per-session PR list/tab was found.

### 1.6 Existing tests that pin related behavior

- `core/tests/session_rail_tests.rs:26-41`: rows tied on last activity sort by session id regardless of write order.
- `scripts/sessionRailRegressions.test.ts:43-50`: an equal background-work reconnect does not replace the rail list; a changed item does.
- `scripts/sessionRailRegressions.test.ts:52-56`: updating a row cannot change its id.
- `scripts/sessionRailRegressions.test.ts:58-66`: removing a row clears selection only when that row was selected.
- `scripts/sessionRailRegressions.test.ts:68-72`: available/resumable sessions keep row metadata but drop History turns.
- `scripts/sessionRailRegressions.test.ts:74-87`: status changes keep the first done time; settle archives; reopen clears both dates.
- `scripts/sessionRailRegressions.test.ts:89-104`: metadata save preserves completion, settle, pin and branch values; absent pin maps to null.
- `scripts/sessionRailRegressions.test.ts:106-118`: recent/name sort ties stay stable across input order and direction.
- `scripts/sessionRailRegressions.test.ts:120-127`: pinned rows follow the active sort.
- `scripts/sessionRailRegressions.test.ts:129-141`: blank titles use the first eight id characters; prompt titles use the trimmed first line and a 64-character limit.
- `scripts/sessionRailRegressions.test.ts:143-153`: the folded session strip keeps working rail order, then done rows newest-finished first, with active marked.
- The elapsed test is `tauri-svelte-preview/scripts/railElapsedTicker.test.ts` (not under `components/`): it clamps negative ages, checks second/minute/hour/day boundaries, and pins the 15-second display sequence (`railElapsedTicker.test.ts:1-23`).
- The other two test paths are `core/tests/session_rail_tests.rs` and `tauri-svelte-preview/scripts/sessionRailRegressions.test.ts`; the latter's rows are grouped by the listed line ranges (`git -C /home/blackcolours/dev/work/mac-command-bar rev-parse HEAD` returned the pinned hash above).

## 2. New GPUI rail today

### Row, data and click

- `desktop/src/features/sessions/session_row.rs` draws the row and receives a `RailEntry`, selection/offline state, current time, open callback and lifecycle callback (`session_row.rs:25-47`).
- `RailEntry` includes session id, kind, section, agent, repo, machine, title, times, state flags, ordering, ask session/sequence, branch, icon, header and preview (`desktop/src/features/sessions/rail_page.rs:38-70`).
- The row renders provider icon or spinner, title, elapsed time or age, state, machine/branch and optional two-line preview (`session_row.rs:85-201`).
- Geometry: padding 9/10/16/14 px (top/bottom/left/right); title 13/18 px; second line 11.5/16 px; preview 12/17 px up to two lines; icon 13 px; selected stripe 2 px (`session_row.rs:113-152,154-200,205-228`).
- Colors use GPUI `caret`, `info`, `warning`, `danger`, `success`, `muted_foreground`; hover/selected use `secondary` at 0.4/0.7 opacity (`session_row.rs:73-83,101-108,217-228`). These are not CSS classes.
- Click selects the session and clears the filter (`session_row.rs:229`; `sessions_pane.rs:341-346`).
- Right-click offers eligible lifecycle moves: `Mark done`, `Settle`, `Back to working`, `Unsettle` (`session_row.rs:234-243`; `desktop/src/features/sessions/status.rs:30-42`).
- There is no ellipsis/archive button, hover flyout or row expansion (`session_row.rs:85-243`).

### Topics and reusable controls

- `SessionsPane` owns GPUI `ListState` and renders virtualized rows (`desktop/src/features/sessions/sessions_pane.rs:44-79,150-154`).
- The pane subscribes to `sessionRail`; invalidation causes a `sessions/rail` read (`sessions_pane.rs:165-210,229-247`).
- A separate `sessions` topic state holds `SessionItem` values (`desktop/src/core/state/sessions.rs:20-21,63-75`); the rail row uses `RailEntry` (`pane_lines.rs:72-105,158-168`).
- Backend registers `sessions` and `sessionRail`, plus `sessions/page` and `sessions/rail` (`dotnet/Assembly.Application/Features/Chat/Sessions/SessionsFeature.cs:19-25,36-66`).
- `ContextMenuExt` and `PopupMenuItem` implement the existing right-click menu (`session_row.rs:9-12,234-243`).
- `Popover` anchors at the rail view-options trigger's top-right (`desktop/src/features/sessions/view_options.rs:4-8,96-105`). It could host hover flyout content; that use is a proposal, not current behavior.
- `DropdownMenu` provides sort choices; `ListState` is GPUI list state, not a gpui-component list widget (`view_options.rs:187-205`; `sessions_pane.rs:65-68,150-154`).
- Locked gpui-component version is 0.8.1 (`desktop/Cargo.lock:2138-2139`).
- `ExpandRow` already provides grow/shrink, 160 ms easing, 40 ms delayed fade, list-height updates and reduced-motion behavior (`desktop/src/core/ui/expand_row.rs:1-10,31-38,137-148,205-265`; `desktop/src/core/theme/grow.rs:1-22`).
- It is used for worktree/chat rows, not rail rows (`desktop/src/features/worktrees/worktrees_pane.rs:63-79`; `desktop/src/features/session/view/ask_row.rs:1-6`). Reuse it for the opened row.

## 3. Backend data and gaps

### Rail, questions and sub-agents

- `sessions` publishes summary items; `sessionRail` invalidates the rail; `sessions/rail` returns grouped rows and a `Needs` count (`SessionsFeature.cs:19-25,36-66,138-149`).
- `RailEntry` has `needsYou`, `askSession`, `askSeq`, but not question text or options (`SessionsFeature.cs:139-146`; `desktop/src/features/sessions/rail_page.rs:38-70`).
- `SessionSummary` keeps `PendingAsk` and counts; it internally tracks open ask ids (`dotnet/Assembly.Application/Features/Chat/Rows/SessionSummaries.cs:6-18,72-124`).
- Keyed `rows` topic and `rows/page` already scope row data by session (`dotnet/Assembly.Application/Features/Chat/Rows/RowsFeature.cs:11-20,51-76,144-156`).
- Ask payload includes kind, state, title, message, tool-call id, options, schema and answer (`desktop/src/features/session/rows/ask.rs:9-23`).
- Smallest fit: `rows/detail` read backed by a targeted `RowStore` query, returning only open question payloads for the opened row; `RowStore` currently queries active task/subagent rows and summaries (`dotnet/Assembly.Application/Features/Chat/Rows/RowStore.cs:168-186`).
- Refresh open details from existing `sessionRail` invalidation; do not add question payloads to every rail entry (`SessionsFeature.cs:19-25`).
- `SessionItem` has `ParentSessionId`; summaries count active `subagent` rows without returning details (`dotnet/Assembly.Application/Features/Chat/Sessions/SessionItem.cs:12-21`; `SessionSummaries.cs:95-123`; `RowStore.cs:168-186`).
- Smallest fit: include current native subagent rows in `rows/detail`; exclude helper sessions as required by TSK-1634 (“Done means”).

### Workflows, PRs and Changes

- Workflow calls expose `board`, `board/page`, `board/changes` and `board/task` (`dotnet/Assembly.Infrastructure/Workflows/WorkflowCalls.cs:14-25`).
- `BoardTask` has `CreatorSessionId`, state, summary, task URL and optional PR URL; `BoardTaskResult` has task, questions, feed and metrics (`dotnet/Assembly.Application/Features/Workflows/Contracts/BoardTask.cs:1-4`; `BoardTaskResult.cs:1-4`).
- `BoardEvent` holds task snapshot, questions and changed paths; it defines no workflow steps (`dotnet/Assembly.Application/Features/Workflows/Contracts/BoardEvent.cs:1-4`).
- Smallest fit for linked Board and PR metadata: add `board/session` in `dotnet/Assembly.Application/Features/Workflows/WorkflowQueries.cs` and map it in `dotnet/Assembly.Infrastructure/Workflows/WorkflowCalls.cs`, filtered by `CreatorSessionId` (`BoardTask.cs:1-4`).
- That read cannot provide step spinner/check/cross state because no cited step source exists (`BoardEvent.cs:1-4`); TSK-1634 defers steps until data exists (“Plan content”).
- Desktop Changes reads working-tree changes, not PRs (`desktop/src/features/changes/changes_pane.rs:1-5`).
- `PrUrl` can be returned for a linked Board task; no session PR list or PR status source exists (`BoardTask.cs:4`). Hide the tab when it has no content per TSK-1634.

### Connect and persistent actions

- Machine state exposes remote reachability (`desktop/src/core/state/machines.rs:1-4,13-20,63-80`).
- Chat calls `machines/connect { machine }` and shows `Connect` / `Connecting…` (`desktop/src/features/session/composer/composer_view.rs:223-241`; `composer_render.rs:184-205`). Rail currently only shows `offline` (`session_row.rs:49-59,154-185`); reuse the existing call.
- `sessions/mark` persists Working / Done / Settled (`SessionsFeature.cs:78-88`).
- `session_info` has no pin or orchestrator fields (`dotnet/Assembly.Application/Features/Chat/Sessions/SessionStore.cs:15-29`).
- Current sort choices are Recent and Name (`desktop/src/features/sessions/view_options.rs:32-45`); custom-order storage is absent from the cited session schema/sort list (`SessionStore.cs:15-29`; `view_options.rs:32-45`). The old app stores order in `rail.session-order`, a local setting, not per-session metadata (`SessionsColumn.svelte:113-123,179-203`).
- Add persisted pin/orchestrator fields and return them in the rail response; keep custom order in the user-setting path rather than the session table. Rename/move/delete are not current `SessionsFeature` calls or `SessionRoutes` routes (`SessionsFeature.cs:36-105`; `dotnet/Assembly.Infrastructure/Http/SessionRoutes.cs:19-24`).
- Exact editor/source-control and clipboard action implementations were not found in either current row (`+page.svelte:182-205`; `session_row.rs:85-243`).

## 4. Proposed implementation cuts

These four lanes run in order. Each targets about 300 changed lines, names the old source it follows, and keeps visible work with Opus and backend work with Sol. The legacy rail has no per-row detail panel or hover flyout; those requested surfaces are new port work, not copied behavior.

### Lane 1 — Backend reads and saved row state

- Model: Sol. Target about 300 changed production and test lines; if reads plus writes need about twice that, stop for a smaller split proposal.
- Files: `RowsFeature.cs`, `RowStore.cs`, `WorkflowQueries.cs`, `WorkflowCalls.cs`, `SessionsFeature.cs`, `SessionStore.cs`, and focused Chat Rows, Chat Sessions and Workflows tests.
- Reads: a session-scoped detail read for open questions and native sub-agents, plus a session-scoped Board/PR summary. Old question/workflow sources are `boardSource.ts:25-58,73-77` and `tauriSource.ts:2307-2323`; old helper counts come from `ownedSessions.ts:175-227`.
- Writes: persist pin and main-orchestrator state. Old pin travels through session metadata (`+page.svelte:175-212`; `tauriSource.ts:2611-2621`); the orchestrator is a machine setting projected onto session rows (`core/src/broker.rs:260-275`; `agent_conversation/manager.rs:3619-3624,3672-3674`). Preserve these different storage homes.
- Ordering: keep custom order as a user preference, matching `rail.session-order` (`SessionsColumn.svelte:113-123,179-203`). The current Assembly backend already stores arbitrary JSON preferences through `PreferenceStore` and `preferences/set`, so use that existing path rather than adding a session field (`PreferencesFeature.cs:7-33`; `PreferenceStore.cs:17-36`).
- No invented workflow-step payload: the old app reads waiting steps with `list_owner_inbox` (`OwnerInbox.svelte:74-86`; `tauriSource.ts:2307-2323`), while the current Assembly backend contracts cited above do not yet expose their state.
- Proposed failing tests: `detail_read_returns_only_open_questions_for_requested_session`; `detail_read_returns_native_subagents_without_helpers`; `board_session_read_returns_only_tasks_created_by_requested_session`; `pin_and_main_orchestrator_restore_from_storage`.
- Verification from `dotnet/`: `MSBUILDDISABLENODEREUSE=1 dotnet test --filter FullyQualifiedName~SessionDetailTests` and `MSBUILDDISABLENODEREUSE=1 dotnet test --filter FullyQualifiedName~SessionRailActionsTests`.

### Lane 2 — Opened-row drop-down

- Model: Opus for visible UI. Target about 300 changed lines.
- Files: `desktop/src/features/sessions/session_row.rs`, `pane_lines.rs`, `mod.rs`, and new `desktop/src/features/sessions/session_detail.rs`.
- Old selection reference: `SessionRowVisual.svelte:13-22,27-65` selects without expanding. Old group expansion and helper rows are in `SessionRail.svelte:253-321,307-317`; they are not an in-row detail panel.
- Content reference: use the existing question input/actions in `BoardQuestions.svelte:111-158` and waiting-step states in `OwnerInbox.svelte:131-153`. These are separate surfaces in the old app; the row's tab order and expansion behavior come from TSK-1634.
- Contract: use `ExpandRow`, load on first open, render only tabs with data, keep selection when details close, and show the row data returned by lane 1. Do not infer PR details from the row's `pullRequest` label alone (`ownedSessions.ts:108-116`).
- Proposed failing tests: `rail_detail_opens_and_loads_once`; `rail_detail_answer_updates_the_open_question`; `rail_detail_close_hides_content_without_changing_selection`.
- Verification from `desktop/`: `cargo test --locked rail_detail`; then the native four-way chat check and feature check on the final commit (Assembly `AGENTS.md`, “Checking a feature”).

### Lane 3 — Menu and row actions

- Model: Opus for visible controls; consume lane 1's saved-state and connection contracts. Target about 300 changed lines for the task-scoped actions.
- Files: `desktop/src/features/sessions/session_row.rs`, `pane_lines.rs`, and the smallest existing menu/action module; no new framework or menu abstraction.
- Copy and disabled-state reference: `sessionRowMenu.ts:66-153`; context-menu placement, Escape/outside dismissal and project submenu: `SessionRowContextMenu.svelte:19-52,63-91`.
- Action dispatch reference: `SessionRail.svelte:201-220`; Pin/Main orchestrator state and Connect are wired in `+page.svelte:214-229,481-495,636-640`. Connect appears on an offline row as `Connect` or disabled `Connecting…` (`WorktreeAgentRow.svelte:324-336`).
- Hover flyout: no old source implementation, copy, delay or dismissal behavior was found (`WorktreeAgentRow.svelte:237-241,268-308`). Treat its visible contents and trigger as task-defined UI; do not claim these details were copied from the old app.
- Proposed failing tests: `rail_menu_actions_target_the_row_session`; `rail_menu_offline_connect_shows_pending_state`; `rail_menu_escape_and_outside_click_close`.
- Verification from `desktop/`: `cargo test --locked rail_menu`; then native four-way chat and feature checks on the final commit.

### Lane 4 — Drag to custom order

- Model: Opus for visible drag interaction; use lane 1's user-preference persistence. Target about 300 changed lines.
- Files: `desktop/src/features/sessions/session_row.rs`, `sessions_pane.rs`, `rail_page.rs`, and `view_options.rs` only where needed.
- Old drag contract: one list-level handler, one group per drag, before/after from the target midpoint, and no cross-group drop (`SessionRail.svelte:141-194,325-333`). Helpers have no drag group and cannot be targets (`SessionRail.svelte:307-319`).
- Old order algorithm: preserve all non-target-group positions and append group rows without saved positions (`myWorkViewOptions.ts:272-295`).
- Old persistence: restore and save the array of owned ids under `rail.session-order`; selecting Custom order is part of the drop (`SessionsColumn.svelte:113-123,179-203`). It is a preference, not session state.
- Proposed failing tests: `rail_drag_reorders_only_within_current_group`; `rail_drag_preserves_other_group_positions`; `rail_drag_restores_saved_order`.
- Verification from `desktop/`: `cargo test --locked rail_drag`; then native four-way chat and feature checks on the final commit.

## Assumptions and unresolved items

- Verified: the old app has a grouped Svelte rail, context menu, Connect button, pin/orchestrator sections, and same-group drag ordering (`SessionRail.svelte:116-194,243-321`; `WorktreeAgentRow.svelte:324-336`; `myWorkViewOptions.ts:223-237`).
- Verified: the old app has no per-session detail dropdown or row hover flyout in the pinned source search described in section 1; it keeps questions/steps, sub-agents and PRs in other surfaces.
- Assumed: `rows/detail` remains the smallest new-backend read for questions and native sub-agents; it is proposed, not implemented (`RowStore.cs:168-186`).
- Verified: the Assembly backend's current Board contracts expose task questions and PR URLs but not workflow-step state (`BoardTask.cs:1-4`; `BoardTaskResult.cs:1-4`; `BoardEvent.cs:1-4`). The old app has a separate inbox command for waiting steps (`OwnerInbox.svelte:74-86`).
- Controller decision: whether the `PRs` tab means linked Board PR URLs or full GitHub PR records. The old rail has no such tab; the full GitHub list is repository-scoped (`PullRequestWorkspace.svelte:122-145,254-287`).
- Controller decision: hover flyout contents and trigger/dismissal details must come from the TSK-1633 plan, because the old row has no flyout implementation.

# Report

Changed only `docs/plans/2026-10-29/recon-rail-row-dropdown.md`; no commit was made. Old app source hash: `d5f5fe68cfe395f6cc7aef0ef6ea57d94147559f`; Assembly source reference hash retained from the prior recon: `fafb665e76fd17a706917f3948247cb08eea5310`.

Tests were not run: this was a source recon and document revision, with no behavior implementation. The named old-app regression tests are mapped in section 1.6.

Not done: no GPUI code, tests, builds, commits, or edits to the old app. Not found: an old row detail dropdown or hover flyout, and a session-scoped PR list. The controller must choose the PR tab contract and use the TSK plan for flyout content and behavior.
