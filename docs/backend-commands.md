# Backend commands of the old UI

Every backend command the old UI (this repo at d9e92be, `app/`) calls, per feature: where it's called, the args it
sends, the answer it reads, the events and streams it listens to, and the old Rust handler in mac-command-bar at
2089290. Each feature's "Defined but not called" list has the handlers nothing in the UI calls.

This is a reference for details such as formats and edge cases. It is not a spec: what a feature should do comes from
the spec Jesse corrects (AGENTS.md, "Porting a feature").

**Fix commits** is a churn hint, not evidence that anything works. It's "fix commits of all commits" for the
handler, counted from mac-command-bar's history to 2089290. A commit counts if it touched the handler's function
(`git log -L`, which loses track where code moved between files) or changed a Rust line naming the command. It's a
fix commit if its message says fix, bug or regression. A high count marks where the back-and-forth has been, so be
careful there.

**Remote routing (every feature).** `workspaceInvoke` = `app/src/lib/workspaceInvoke.ts:10`. Any command sent through it whose args hold a machine-qualified path (`assembly-remote://<profileId>/<abs path>`) under the keys `root, path, directory, source, target, roots, paths, projectRoot, projectPath, worktreePath, checkoutPath, gitRoot, localRoot, projectFilter` (and, for the six hosted-PR commands, the same keys one level inside the single request object) is NOT sent as itself: it goes to `remote_workspace` with `operation = <command name>` and the paths stripped to plain absolute paths (`app/src/lib/workspaceInvoke.ts:15-19`, `app/src/lib/workspacePaths.ts:72-95`). The answer's path-keyed strings are re-qualified on the way back (`qualifyWorkspaceResult`, `workspacePaths.ts:97`). Commands whose args carry no such top-level key (e.g. `{ preview, request }` for the language-server calls) are never remote-routed. If the remote dispatcher has no arm for the operation it answers `"<op> is not available for remote workspaces"` (`.../agent_conversation/remote/workspace.rs:107`). "remote arm" below = the line of that dispatcher's match arm. All Rust paths are under `tauri-svelte-preview/src-tauri/src/`; `…/remote/` = `agent_conversation/remote/`. Where a TS wrapper in `tauriSource.ts` exists but nothing imports it, the cell says "wrapper never called".

## Chat, projects and layout
Starting, running, stopping, reading and managing agent conversations (including attachments, drafts, history paging, per-session workspace layout), the projects registry, and the close/quit hold.

Path shorthand: `RS` = `tauri-svelte-preview/src-tauri/src`; `UI` = `app/src/lib`. Most calls go through `UI/workspaceInvoke.ts:10` (reroutes to `remote_workspace` only when a path arg is machine-qualified) or `UI/shell/conversation/conversationInvoke.ts` (plain `invoke` plus typed error logging).

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `ensure_agent_conversation` | `app/src/lib/shell/conversation/conversationService.ts:1437` | `{ request: { ownedId, executionEnvironment, remoteProfileId?, provider, cwd, nativeSessionId?, nativeSessionMode: 'resume'\|'load', reasoningEffort?, projectId? } }` | `AgentConversationConnection { ownedId, provider, generation, nativeSessionId?, state, suspended?, config? }` | `RS/agent_conversation/mod.rs:109` (remote: `RS/agent_conversation/remote/server/mod.rs:331`) | 3 of 9 |
| `send_agent_conversation_message` | `app/src/lib/shell/conversation/conversationService.ts:1691` | `{ request: { ownedId, generation, text, content, attachmentIds, model, approvalPolicy } }` | `AgentConversationSendReceipt { ownedId, generation, turnId, userItemId, admittedSequence }` | `RS/agent_conversation/mod.rs:143` (remote: `server/mod.rs:488`) | 1 of 12 |
| `run_agui` | `app/src/lib/shell/conversation/conversationService.ts:1501, 1690` | `{ input: { threadId, runId, parentRunId?, resume?, messageId?, message?: <same request as send> } }` | `AgentConversationSendReceipt \| null` (resume call ignores answer) | `RS/agent_conversation/agui.rs:328` | 1 of 3 |
| `stop_agent_conversation_turn` | `app/src/lib/shell/conversation/conversationService.ts:1630, 1665, 1717` | `{ request: { ownedId, generation } }` | void | `RS/agent_conversation/mod.rs:589` (remote: `server/mod.rs:574`) | 0 of 4 |
| `close_agent_conversation` | `app/src/lib/shell/conversation/conversationService.ts:1372` | `{ ownedId, generation }` | `boolean` | `RS/agent_conversation/mod.rs:772` | 1 of 5 |
| `cancel_agent_conversation_request` | `app/src/lib/tauriSource.ts:2107` | `{ requestId: number }` | void | `RS/agent_conversation/mod.rs:859` | 5 of 9 |
| `list_agent_conversation_sessions` | `app/src/lib/tauriSource.ts:2238` | none | `AgentConversationSessionRecord[]` | `RS/agent_conversation/mod.rs:915` (remote: `server/mod.rs:324`) | 2 of 8 |
| `list_remote_agent_conversation_sessions` | `app/src/lib/tauriSource.ts:2361` | `{ requestId }` | `AgentConversationSessionRecord[]` | `RS/agent_conversation/mod.rs:941` | 1 of 6 |
| `list_agent_sessions` | `app/src/lib/tauriSource.ts:2002` | `{ remoteProfileId: string\|null }` | `AgentSession[]` | `RS/commands/agent_sessions.rs:17` | 1 of 6 |
| `list_agent_sessions_for_project` | `app/src/lib/tauriSource.ts:2010` | `{ projectPath }` | `AgentSession[]` | `RS/commands/agent_sessions.rs:34` | 1 of 6 |
| `read_agent_session_details` | `app/src/lib/tauriSource.ts:2019` | `{ logPath, remoteProfileId }` | `AgentSession[]` | `RS/commands/agent_sessions.rs:46` | 2 of 5 |
| `read_remote_saved_session_details` | `app/src/lib/tauriSource.ts:2035` | `{ profileId, ownedId }` | `RemoteSavedSessionDetails { turnCount, firstPrompt, startComplete, latestTurns }` | `RS/commands/agent_sessions.rs:65` | 0 of 1 |
| `set_main_orchestrator` | `app/src/lib/tauriSource.ts:2245` | `{ ownedId: string\|null, remoteProfileId: string\|null }` | void | `RS/agent_conversation/mod.rs:873` | 11 of 18 |
| `change_agent_conversation_checkout` | `app/src/lib/tauriSource.ts:2504` | `{ request: { ownedId, generation, cwd } }` | `AgentConversationSessionRecord` | `RS/agent_conversation/mod.rs:606` (remote: `server/mod.rs:462`) | 0 of 2 |
| `update_agent_conversation_session_meta` | `app/src/lib/tauriSource.ts:2619` | `{ request: { ownedId, model, effort, meta } }` | `AgentConversationSessionRecord` | `RS/agent_conversation/mod.rs:1047` (remote: `server/mod.rs:468`) | 1 of 4 |
| `delete_agent_conversation_session` | `app/src/lib/tauriSource.ts:2629` | `{ ownedId }` | `boolean` (something was deleted) | `RS/agent_conversation/mod.rs:787` (remote: `server/mod.rs:250, 481`) | 1 of 3 |
| `set_session_project` | `app/src/lib/shell/projects/projectRegistry.svelte.ts:58` | `{ ownedId, projectId }` | void | `RS/agent_conversation/session_project.rs:27` | 1 of 1 |
| `begin_agent_conversation_import` | `app/src/lib/tauriSource.ts:2800` | `{ remoteProfileId: null, provider, nativeSessionId, transcriptPath, cwd, title?, remoteProfileId? }` | `[ownedId: string, rebuilt: boolean]` | `RS/agent_conversation/mod.rs:1118` (remote: `server/mod.rs:364`) | 1 of 8 |
| `finish_agent_conversation_import` | `app/src/lib/tauriSource.ts:2813` | `{ ownedId }` | `number` (events added) | `RS/agent_conversation/mod.rs:1136` | 1 of 3 |
| `extend_agent_conversation_import` | `app/src/lib/tauriSource.ts:2833` | `{ ownedId }` | `{ added: number, reachedStart: boolean }` | `RS/agent_conversation/mod.rs:1149` (remote: `server/mod.rs:346`) | 1 of 6 |
| `save_agent_conversation_attachment` | `app/src/lib/shell/conversation/conversationService.ts:337` | `{ ownedId, mimeType, bytes: base64 string }` | `SavedAttachment { id, path, byteLength, thumbnail*, ... }` | `RS/agent_conversation/mod.rs:1190` | 1 of 5 |
| `read_agent_conversation_attachments` | `app/src/lib/shell/conversation/conversationService.ts:458`, `app/src/lib/shell/panels/context/SessionContextPanel.svelte:126` | `{ ownedId }` | `SavedAttachment[]` | `RS/agent_conversation/mod.rs:1216` (remote: `server/mod.rs:192`) | 2 of 6 |
| `read_agent_conversation_selected_attachments` | `app/src/lib/shell/conversation/conversationService.ts:958` | `{ ownedId, attachmentIds: string[] }` | `SavedAttachment[]` | `RS/agent_conversation/mod.rs:1233` | 1 of 1 |
| `read_agent_conversation_attachment_file` | `app/src/lib/shell/conversation/conversationService.ts:370`, `app/src/lib/shell/conversation/components/AttachmentLightbox.svelte:89` | `{ ownedId, attachmentId, thumbnail: boolean, byteLength, mimeType, transferId }` | `string` (local file path) | `RS/agent_conversation/mod.rs:1259` (remote: `server/mod.rs:210`) | 0 of 2 |
| `discard_agent_conversation_original` | `app/src/lib/shell/conversation/components/AttachmentLightbox.svelte:49` | `{ ownedId, attachmentId, mimeType, transferId }` | void | `RS/agent_conversation/mod.rs:1330` | 0 of 2 |
| `delete_agent_conversation_attachment` | `app/src/lib/shell/conversation/conversationService.ts:348, 528` | `{ request: { ownedId, attachmentId, path } }` | void | `RS/agent_conversation/mod.rs:1358` (remote: `server/mod.rs:217`) | 3 of 6 |
| `agent_conversation_set_session_draft` | `app/src/lib/shell/conversation/conversationService.ts:154` | `{ ownedId, text }` | void | `RS/agent_conversation/mod.rs:173` (remote: `server/mod.rs:426`) | 1 of 5 |
| `agent_conversation_get_session_draft` | `app/src/lib/shell/conversation/conversationService.ts:155` | `{ ownedId }` | `string \| null` | `RS/agent_conversation/mod.rs:187` (remote: `server/mod.rs:430`) | 1 of 5 |
| `agent_conversation_clear_session_draft` | `app/src/lib/shell/conversation/conversationService.ts:156` | `{ ownedId }` | void | `RS/agent_conversation/mod.rs:200` (remote: `server/mod.rs:433`) | 1 of 5 |
| `read_agent_conversation_history_download` | `app/src/lib/tauriSource.ts:2166` | `{ ownedId }` | `HistoryDownloadProgress \| null` | `RS/agent_conversation/mod.rs:815` | 0 of 1 |
| `read_agent_conversation_child_history` | `app/src/lib/tauriSource.ts:2220` | `{ parentOwnedId, childSessionId, requestId, maxBytes }` | `AgentConversationChildHistory` | `RS/agent_conversation/mod.rs:1060` | 1 of 4 |
| `stop_agent_conversation_child_history` | `app/src/lib/tauriSource.ts:2232` | `{ parentOwnedId, requestId }` | `boolean` | `RS/agent_conversation/mod.rs:1091` | 1 of 1 |
| `read_agent_conversation_tool_call` | `app/src/lib/tauriSource.ts:2592` | `{ ownedId, itemId }` | `{ output: string\|null, diff: string\|null }` | `RS/agent_conversation/manager/tool_call.rs:105` | 0 of 1 |
| `list_agent_conversation_events` | `app/src/lib/tauriSource.ts:2515` | `{ ownedId, fromSequence }` | `AgentConversationEvent[]` | `RS/agent_conversation/mod.rs:956` | 2 of 4 |
| `list_agent_conversation_events_before` | `app/src/lib/tauriSource.ts:2528` | `{ ownedId, beforeSequence, maxBytes }` | `{ events: AgentConversationEvent[], hasMore: boolean }` | `RS/agent_conversation/mod.rs:966` | 2 of 5 |
| `read_agui_snapshot` | `app/src/lib/tauriSource.ts:2184` (via `readAgentConversationSelectionFromTauri`) | `{ ownedId, requestId, maxBytes, minimumGeneration? }` | `AguiSnapshot { page: { beforeCursor?, afterCursor?, ... }, ... } \| null`; also opens the session's AG-UI view | `RS/agent_conversation/agui.rs:362` | 2 of 5 |
| `list_agui_events_before` | `app/src/lib/tauriSource.ts:2552` | `{ ownedId, beforeSequence, maxBytes, requestId }` | `AguiPage { beforeCursor?, afterCursor?, ... }` | `RS/agent_conversation/agui.rs:398` | 1 of 3 |
| `list_agui_events_after` | `app/src/lib/tauriSource.ts:2576` | `{ ownedId, afterSequence, maxBytes, requestId }` | `AguiPage` | `RS/agent_conversation/agui.rs:412` | 0 of 2 |
| `close_agui_view` | `app/src/lib/tauriSource.ts:2608` | `{ ownedId }` | void (errors only warned) | `RS/agent_conversation/agui.rs:320` | 1 of 1 |
| `handoff_agent_conversation` | `app/src/lib/shell/conversation/conversationService.ts:686` | `{ request: { ownedId, generation, direction, mode, phase, expectedOwner?, targetOwnedId?, nativeSessionId?, ptySessionId?, historyBoundary, processTree } }` | `AgentConversationHandoffReceipt { ownedId, generation, direction, mode, phase, previousOwner, owner, ..., rollbackAvailable, message }` | `RS/agent_conversation/handoff.rs:140` | 1 of 3 |
| `read_agent_conversation_capabilities` | `app/src/lib/tauriSource.ts:2082` | `{ ownedId, requestId }` | `AgentConversationCapabilities` | `RS/agent_conversation/mod.rs:758` (remote: `server/mod.rs:400`) | 2 of 4 |
| `read_agent_conversation_config` | `app/src/lib/shell/conversation/conversationConfig.ts:80` | `{ ownedId, requestId }` | `AgentConversationConfigState { model, availableModels, reasoningEffort, availableEfforts, approvalPolicy, availableApprovalPolicies }` | `RS/agent_conversation/mod.rs:744` (remote: `server/mod.rs:403`) | 1 of 4 |
| `warm_agent_conversation_config` | `app/src/lib/shell/conversation/conversationConfig.ts:105` | `{ ownedId, generation }` | `AgentConversationConfigState` | `RS/agent_conversation/mod.rs:684` | 1 of 2 |
| `set_agent_conversation_config` | `app/src/lib/shell/conversation/conversationConfig.ts:115` | `{ request: { ownedId, generation, model?, reasoningEffort?, approvalPolicy? } }` | `AgentConversationConfigState` | `RS/agent_conversation/mod.rs:624` (remote: `server/mod.rs:413`) | 0 of 4 |
| `set_agent_conversation_config_option` | `app/src/lib/shell/conversation/conversationService.ts:578` | `{ request: { ownedId, generation, optionId, value } }` | `{ capabilities?: AgentCapabilities, configOptions?: AgentConfigOption[] }` | `RS/agent_conversation/mod.rs:652` (remote: `server/mod.rs:417`) | 0 of 4 |
| `probe_agent_provider_config` | `app/src/lib/tauriSource.ts:2126` | `{ provider, executionEnvironment, remoteProfileId, cwd, requestId }` | `AgentConversationConfigState` | `RS/agent_conversation/mod.rs:702` (remote: `server/mod.rs:309`) | 0 of 1 |
| `cancel_agent_provider_probe` | `app/src/lib/tauriSource.ts:2122` | `{ requestId }` | void | `RS/agent_conversation/mod.rs:732` | 0 of 1 |
| `write_agent_conversation_workspace` | `app/src/lib/tauriSource.ts:2639` | `{ ownedId, snapshotJson: string }` | void | `RS/agent_conversation/mod.rs:212` | 0 of 2 |
| `read_agent_conversation_workspace` | `app/src/lib/tauriSource.ts:2651` | `{ ownedId }` | `string \| null` (JSON snapshot, parsed by UI) | `RS/agent_conversation/mod.rs:225` (remote: `server/mod.rs:444`) | 0 of 2 |
| `read_agent_conversation_workspace_expanded_paths` | `app/src/lib/tauriSource.ts:2674` | `{ ownedId, root, requestId }` | `string[]` | `RS/agent_conversation/mod.rs:237` (remote: `server/mod.rs:447`) | 2 of 3 |
| `write_agent_conversation_workspace_expanded_paths` | `app/src/lib/tauriSource.ts:2696` | `{ ownedId, root, paths: string[] }` | void | `RS/agent_conversation/mod.rs:251` | 1 of 2 |
| `delete_agent_conversation_workspace` | `app/src/lib/tauriSource.ts:2706` | `{ ownedId }` | void | `RS/agent_conversation/mod.rs:265` (remote: `server/mod.rs:458`) | 0 of 2 |
| `clear_agent_conversation_workspace_editors` | `app/src/lib/tauriSource.ts:2712` | none | void | `RS/agent_conversation/mod.rs:277` | 0 of 1 |
| `clear_agent_conversation_workspace_tabs` | `app/src/lib/tauriSource.ts:2718` | none | void | `RS/agent_conversation/mod.rs:284` | 0 of 1 |
| `list_projects` | `app/src/lib/tauriSource.ts:3485` | none | `ProjectRecord[] { id, machine, rootPath, title, repoKey, createdAtMs, groupKey, pinnedAtMs, lastUsedMs }` | `RS/agent_conversation/mod.rs:336` | 0 of 3 |
| `add_project` | `app/src/lib/tauriSource.ts:3498` | `{ machine, path, create: boolean, start?: 'git'\|{clone: string} }` | `ProjectRecord` (+ `alreadyExisted?`) | `RS/agent_conversation/mod.rs:375` | 0 of 8 |
| `rename_project` | `app/src/lib/tauriSource.ts:3504` | `{ id, name }` | void | `RS/agent_conversation/mod.rs:441` | 0 of 3 |
| `set_project_root` | `app/src/lib/tauriSource.ts:3510` | `{ id, root }` | void | `RS/agent_conversation/mod.rs:455` | 0 of 3 |
| `set_project_pinned` | `app/src/lib/tauriSource.ts:3515` | `{ id, pinned: boolean }` | void | `RS/agent_conversation/mod.rs:470` | 0 of 3 |
| `remove_project` | `app/src/lib/tauriSource.ts:3521` | `{ id, deleteSessions: boolean }` | void | `RS/agent_conversation/mod.rs:480` | 0 of 5 |
| `list_folders` | `app/src/lib/tauriSource.ts:3528` (local); for a remote machine it sends `remote_workspace { profileId, operation: 'list_folders', args: { path } }` at `:3529` | `{ path }` | `FolderListing { path, directories: string[], truncated }` | `RS/project_folders.rs:273` (remote arm: `RS/agent_conversation/remote/workspace.rs:27`) | 0 of 2 |
| `validate_project_root` | `app/src/lib/tauriSource.ts:793` | `{ path, generation: number }` | `ProjectRootValidationResult \| null` | `RS/files/commands.rs:106` | 1 of 7 |
| `cancel_project_root_validation` | `app/src/lib/tauriSource.ts:807` | `{ generation }` | void | `RS/files/commands.rs:125` | 1 of 3 |
| `project_root_is_bare` | `app/src/lib/shell/conversation/newSession/newSessionBackend.ts:113` (local direct; remote via `remote_workspace`) | `{ root }` | `boolean` | `RS/project_folders.rs:285` (remote arm: `RS/agent_conversation/remote/workspace.rs:70`) | 0 of 3 |
| `continue_app_exit` | `app/src/lib/tauriSource.ts:924` (used by `app/src/lib/shell/components/ShellOverlays.svelte:108`) | `{ kind: 'close'\|'quit' }` | void | `RS/main.rs:3297` | 0 of 3 |
| `cancel_app_exit` | `app/src/lib/tauriSource.ts:930` (used by `ShellOverlays.svelte:192`) | none | void | `RS/main.rs:3291` | 0 of 1 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| `register_agent_conversation_stream` / `acknowledge_agent_conversation_stream` / `unregister_agent_conversation_stream` (args `{ registrationId, channel }`, `{ registrationId, frames, bytes }`, `{ registrationId }`) | channel | envelope `{ session, generation, sequence, bytes, kind: 'agent-conversation-event', resync: boolean, chunk: AgentConversationEvent \| null }`; `resync` or null chunk means "reread"; UI acks each envelope with `frames: 1, bytes` | `app/src/lib/tauriSource.ts:1250-1330` (register at `:1290`); consumer `app/src/lib/shell/conversation/conversationService.ts:1001` | `RS/projection_streams.rs:523, 532, 542` |
| `register_agui_stream` / `acknowledge_agui_stream` / `unregister_agui_stream` (same args) | channel | same envelope, `chunk: StreamChunk[]` (AG-UI chunks for the open chat views) | `app/src/lib/tauriSource.ts:1337`; consumer `conversationService.ts:1006` | `RS/agent_conversation/agui.rs:304, 309, 314` |
| `agent-conversation-history-download` | event | `HistoryDownloadProgress { profileId, sourceOwnedId, workspaceOwnedId, childSessionId, totalBytes, coveredBytes, phase: 'downloading'\|'paused'\|'complete'\|'error', ready, error }` | `app/src/lib/tauriSource.ts:2155`; used `conversationService.ts:801` | `RS/main.rs:3461` |
| `session-title-changed` | event | `{ ownedId, title }` | `app/src/lib/shell/conversation/conversationService.ts:1013` | `RS/main.rs:3545` (const `:560`) |
| `remote-connection-changed` | event | `{ profileId, state: RemoteConnectionState }` | `app/src/lib/shell/conversation/conversationService.ts:1019` | `RS/main.rs:3455` (const `:566`) |
| `assembly_exit_requested` | event | `{ kind: 'close'\|'quit' }` | `app/src/lib/tauriSource.ts:918`; used `app/src/lib/shell/components/ShellOverlays.svelte:118` | `RS/main.rs:3864` (close), `RS/main.rs:3886` (quit) |

### Defined but not called by the UI
- `respond_agent_conversation_approval` — `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:515` (remote arm exists: `remote/server/mod.rs:536`)
- `respond_agent_conversation_permission` — `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:538`
- `respond_agent_conversation_input` — `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:561`
- `list_agent_conversation_items_before` — `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:980` (UI's `listAgentConversationItemsBeforeFromTauri` actually calls `list_agui_events_before`)
- `list_agent_conversation_items_after` — `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:1013` (UI's `...ItemsAfter...` calls `list_agui_events_after`)
- `read_agent_conversation_selection` — `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:823` (UI's `readAgentConversationSelectionFromTauri` calls `read_agui_snapshot`)
- `agent_conversation_add_session_annotation` — `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:71`
- `agent_conversation_list_session_annotations` — `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:87`
- `agent_conversation_delete_session_annotation` — `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:100`

## Remote machines
Saved SSH machines the app can install its backend on, connect to, and then run files, git and workflow requests on, plus pairing a phone with a machine.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `read_remote_assembly_environment` | `app/src/lib/tauriSource.ts:2377`, `app/src/lib/shell/settings/ProviderList.svelte:15`, `app/src/lib/shell/messages/AgentMessages.svelte:156` | none | `{ profiles: RemoteAssemblyProfile[], readyProfileIds: string[] }`; profile = `{ id, name, sshTarget, sourceRoot, defaultCwd }` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:2516` | 0 of 3 |
| `read_remote_backend_statuses` | `app/src/lib/tauriSource.ts:2385`, `app/src/lib/shell/connections/RemoteConnections.svelte:62` | `{ profiles: RemoteAssemblyProfile[] }` | `{ profileId, installed, installedVersion: string?, backendBuildKind: 'release'/'development', backendCommit: string?, latestVersion, updateAvailable: boolean? }[]` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:2621` | 0 of 2 |
| `connect_remote_assembly` | `app/src/lib/tauriSource.ts:2421`, `app/src/lib/shell/connections/RemoteConnections.svelte:113`, `app/src/lib/shell/projects/ProjectsScreen.svelte:128` | `{ profile: RemoteAssemblyProfile, operationId: string (uuid), status: Channel<string> }` | `{ profile, sessions: AgentConversationSessionRecord[], replacedProfileId: string? }`; progress lines arrive on `status` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:2523` | 0 of 5 |
| `install_remote_assembly` | `app/src/lib/tauriSource.ts:2469`, `app/src/lib/shell/connections/RemoteConnections.svelte:113` | `{ profile: RemoteAssemblyProfile, operationId: string (uuid), status: Channel<string> }` | same as connect: `{ profile, sessions, replacedProfileId }`; `status` carries text lines or a JSON string `{ kind:'transfer', stage:'download', bytes, total, bytesPerSecond?, etaSeconds? }` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/install_flow.rs:18` | 1 of 4 |
| `uninstall_remote_assembly` | `app/src/lib/tauriSource.ts:2492`, `app/src/lib/shell/connections/RemoteConnections.svelte:194` | `{ profile: RemoteAssemblyProfile, deleteData: boolean }` | `{ environment: RemoteAssemblyEnvironment, replacedProfileId: string? }` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:2649` | 1 of 4 |
| `deploy_remote_backend_dev` | `app/src/lib/tauriSource.ts:2482`, `app/src/lib/shell/connections/RemoteConnections.svelte:175` | `{ destination: string (sshTarget), onEvent: Channel<string> }` | void; output lines arrive on `onEvent`. Registered only in debug builds (`#[cfg(debug_assertions)]`) | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:2557` | 0 of 4 |
| `disconnect_remote_assembly` | `app/src/lib/tauriSource.ts:2398`, `app/src/lib/shell/connections/RemoteConnections.svelte:158`, `app/src/lib/shell/projects/ProjectsScreen.svelte:125` | `{ profileId: string }` | `RemoteAssemblyEnvironment` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:2670` | 0 of 3 |
| `remove_remote_assembly_profile` | `app/src/lib/tauriSource.ts:2393`, `app/src/lib/shell/connections/RemoteConnections.svelte:157` | `{ profileId: string }` | `RemoteAssemblyEnvironment` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:2678` | 0 of 4 |
| `cancel_remote_connection` | `app/src/lib/tauriSource.ts:2431` | `{ operationId: string }` (same id given to connect/install) | void; a failed cancel is only logged | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:2665` | 0 of 3 |
| `remote_workspace` | `app/src/lib/workspaceInvoke.ts:19`, `app/src/lib/tauriSource.ts:3529`, `app/src/lib/shell/conversation/newSession/newSessionBackend.ts:123` | `{ profileId: string, operation: string, args: object, requestId: number? }` | JSON the named operation returns (typed per operation in the other tables); error string on failure | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:5430` -> `RemoteConnectionManager::workspace_operation` `…/remote/mod.rs:1428` -> wire `RemoteCommand::Workspace` `…/remote/wire.rs:37` -> server `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/server/mod.rs:308` -> `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/workspace.rs:21` (`execute`). Operations it handles: list_folders, discard_git_paths, discard_all_git_changes, list_git_branches, create_git_branch, switch_git_branch, stash_git_changes, pop_git_stash, list_git_stashes, amend_git_commit, project_git_status, read_source_git_diff, read_git_branch_diff, stage_git_paths, unstage_git_paths, commit_git_repository, fetch_git_repository, pull_git_repository, push_git_repository, workspace_git_clone (no UI caller), add_git_remote, publish_git_repository, read_git_commit_history, read_git_commit_files, read_git_commit_file_diff, list_project_worktrees, list_repository_checkouts, remove_project_worktree, archive_project_worktree, project_root_is_bare, the six hosted-PR commands, validate_project_root, list_source_directory, list_source_files, search_source_tree, find_source_definitions_in_root, find_source_references_in_root, read_source_file, read_source_image, write_source_file, workspace_exists, workspace_mkdir, workspace_git_init, workspace_rename, workspace_copy_file, workspace_write_text, workspace_read_dir, move_to_trash | 0 of 5 |
| `remote_workflow` (generic; the workflow operations belong to Workflows) | `app/src/lib/tauriSource.ts:2931`, `app/src/lib/shell/components/PairPhonePanel.svelte:15` | `{ profileId: string, operation: string, args: object, requestId: number? }` | JSON the named operation returns | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:5441` -> `workflow_operation` `…/remote/mod.rs:1419` -> server `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/server/mod.rs:269` (phone operations are answered first, anything else goes to the workflow engine) | 1 of 4 |
| `remote_workflow` operation `phonePairing` | `app/src/lib/shell/components/phonePairing.ts:19` | `{ profileId, operation:'phonePairing', args:{ machineName: string } }` | `{ url: string, machineName: string, code: string, expiresAt: number (ms) }`; each call replaces the previous code | `tauri-svelte-preview/src-tauri/src/agent_conversation/phone/mod.rs:174` (arm; `start_pairing` at `…/phone/mod.rs:189`) | 0 of 3 |
| `remote_workflow` operation `phoneDevices` | `app/src/lib/shell/components/phonePairing.ts:13` | `{ profileId, operation:'phoneDevices', args:{} }` | `{ devices: { id, name, createdAt: number, lastSeenAt: number? }[] }` | `tauri-svelte-preview/src-tauri/src/agent_conversation/phone/mod.rs:175` | 0 of 3 |
| `remote_workflow` operation `phoneRevoke` | `app/src/lib/shell/components/PairPhonePanel.svelte:49` | `{ profileId, operation:'phoneRevoke', args:{ deviceId: string } }` | result not read (arm returns `{ revoked }`) | `tauri-svelte-preview/src-tauri/src/agent_conversation/phone/mod.rs:176` | 0 of 4 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| `status` arg of `connect_remote_assembly` | channel | `string` progress line | `app/src/lib/tauriSource.ts:2414` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:930` (`connect_profile_inner_locked`; also lines 908, 942) |
| `status` arg of `install_remote_assembly` | channel | `string`, or JSON text `{ kind:'transfer', stage:'download', bytes, total, bytesPerSecond?, etaSeconds? }` | `app/src/lib/tauriSource.ts:2455` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:861` (`install_and_connect_profile`, starts at 834); transfer JSON built at `…/remote/install.rs:499` |
| `onEvent` arg of `deploy_remote_backend_dev` | channel | `string` script output line | `app/src/lib/tauriSource.ts:2481` | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:2582` (also 2588) |
| `remote-connection-changed` | event | `{ profileId: string, state: 'connected'/'disconnected'/'reconnecting' }` | `app/src/lib/shell/conversation/conversationService.ts:1019` | `tauri-svelte-preview/src-tauri/src/main.rs:3455` (constant `main.rs:566`; payload struct `RemoteConnectionStatus` `…/remote/mod.rs:415`) |

### Defined but not called by the UI
- None in the connect/install/profile code. (`workspace_git_clone` is a `remote_workspace` operation, `…/remote/workspace.rs:57`, that nothing in the UI sends; `inspect_project_folder` is another, `…/remote/workspace.rs:27`, also not sent.)

## Files
Reading, listing, searching, writing, renaming, copying and trashing files in a project folder (on this Mac or a remote machine), plus saved chat image attachments.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `list_source_directory` | `app/src/lib/tauriSource.ts:861`, `app/src/lib/shell/files/explorer/explorerService.ts:89`, `app/src/lib/shell/files/components/EditorPanel.svelte:284` | `{ root: string, directory: string, includeExcluded: boolean, scanId: string? }` | `SourceDirectoryEntry[]` = `{ path, name, isDirectory, excluded }` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:66`; remote arm `…/remote/workspace.rs:85` | 0 of 6 |
| `search_source_tree` | `app/src/lib/tauriSource.ts:896`, `app/src/lib/shell/files/components/FilesPanel.svelte:705` | `{ root, query: string (trimmed), pageSize: number, cursor: number?, includeExcluded: boolean, scanId: string? }` | `{ matches: (SourceDirectoryEntry + { relativePath })[], nextCursor: number?, complete: boolean }` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:274`; remote arm `…/remote/workspace.rs:87` | 0 of 6 |
| `list_source_files` | `app/src/lib/tauriSource.ts:829` (wrapper never called) | `{ root, limit: number, query: string?, scanId: string? }` | `{ records: SourceRecord[], limit, truncated: boolean, stats? }`; `SourceRecord` = `{ path, relativePath, fileName, language, byteCount }` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:35`; remote arm `…/remote/workspace.rs:86` | 1 of 15 |
| `search_source_files` | `app/src/lib/tauriSource.ts:3014` (wrapper never called) | `{ records: SourceRecord[], query: string, limit: number }` | `SourceSearchMatch[]` = `SourceRecord + { line, column, excerpt }` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:263`; no remote arm (no top-level path key) | 0 of 3 |
| `cancel_source_scan` | `app/src/lib/tauriSource.ts:909`, `app/src/lib/shell/files/explorer/explorerService.ts:43`, `app/src/lib/shell/files/components/FilesPanel.svelte:682` | `{ scanId: string }` | `boolean` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:98` | 0 of 3 |
| `read_source_file` | `app/src/lib/tauriSource.ts:1388`, `app/src/lib/shell/files/components/EditorPanel.svelte:912`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:1090` | `{ path: string, generation: number }` | `SourcePreview?` = `SourceRecord + { content, lineCount, revision (SHA-256 of bytes read) }` (UI then overwrites relativePath, language, byteCount from the record it asked about) | `tauri-svelte-preview/src-tauri/src/files/commands.rs:133`; remote arm `…/remote/workspace.rs:90` (always `Some`) | 1 of 9 |
| `cancel_source_file_reads` | `app/src/lib/tauriSource.ts:1418`, `app/src/lib/shell/files/components/EditorPanel.svelte:1428` | `{ generation: number }` | void | `tauri-svelte-preview/src-tauri/src/files/commands.rs:162` | 1 of 3 |
| `read_source_image` | `app/src/lib/tauriSource.ts:1407`, `app/src/lib/shell/files/components/SourceMarkdownPreview.svelte:143` | `{ path: string }` (+ AbortSignal; a remote call sends a requestId so abort cancels it) | `ArrayBuffer` of raw image bytes | `tauri-svelte-preview/src-tauri/src/files/commands.rs:152`; remote arm `…/remote/workspace.rs:91` | 0 of 5 |
| `write_source_file` | `app/src/lib/tauriSource.ts:1510`, `app/src/lib/shell/files/components/EditorPanel.svelte:1075` | `{ path: string, content: string, expectedRevision: string }` | `SourcePreview` (new revision); a save must present the revision it read | `tauri-svelte-preview/src-tauri/src/files/commands.rs:189`; remote arm `…/remote/workspace.rs:92` | 0 of 7 |
| `open_source_file` | `app/src/lib/tauriSource.ts:1523` (via `runPathCommand` `tauriSource.ts:3412`; wrapper never called) | `{ path: string }` | void (wrapper returns `true`) | `tauri-svelte-preview/src-tauri/src/files/commands.rs:202` | 0 of 3 |
| `reveal_source_file` | `app/src/lib/tauriSource.ts:1527` (via `runPathCommand`; wrapper never called) | `{ path: string }` | void | `tauri-svelte-preview/src-tauri/src/files/commands.rs:211` | 0 of 4 |
| `open_path` | `app/src/lib/tauriSource.ts:1531` (via `runPathCommand`), `app/src/lib/shell/history/components/HistoryPanel.svelte:569` | `{ path: string }` | void | `tauri-svelte-preview/src-tauri/src/files/commands.rs:220` | 0 of 3 |
| `reveal_path` | `app/src/lib/tauriSource.ts:1535` (via `runPathCommand`), `app/src/lib/shell/history/components/HistoryPanel.svelte:572`, `app/src/lib/shell/git/components/sourceControl/ChangedFileRow.svelte:144` | `{ path: string }` | void | `tauri-svelte-preview/src-tauri/src/files/commands.rs:229` | 0 of 3 |
| `move_to_trash` | `app/src/lib/tauriSource.ts:1549`, `app/src/lib/shell/files/components/FilesPanel.svelte:945` | `{ path: string }` | void; rejects when the path is outside the folders the window may change | `tauri-svelte-preview/src-tauri/src/files/commands.rs:386`; remote arm `…/remote/workspace.rs:106` | 0 of 6 |
| `workspace_exists` | `app/src/lib/workspaceFs.ts:5`, `app/src/lib/shell/files/components/FilesPanel.svelte:837` | `{ path: string }` (remote paths only; a Mac path uses `@tauri-apps/plugin-fs` `exists`, no backend call) | `boolean` | none found as a Tauri command; remote arm only: `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/workspace.rs:93` | 0 of 2 |
| `workspace_mkdir` | `app/src/lib/workspaceFs.ts:8`, `app/src/lib/shell/files/components/FilesPanel.svelte:869`, `app/src/lib/shell/files/components/FilesPanel.svelte:890` | `{ path: string }` (remote only; Mac uses plugin-fs `mkdir`) | void | none found as a Tauri command; remote arm only: `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/workspace.rs:94` | 0 of 5 |
| `workspace_write_text` | `app/src/lib/workspaceFs.ts:11`, `app/src/lib/shell/files/components/FilesPanel.svelte:870` | `{ path: string, content: string }` (remote only; Mac uses plugin-fs `writeTextFile`) | void | none found as a Tauri command; remote arm only: `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/workspace.rs:98` | 0 of 2 |
| `workspace_read_dir` | `app/src/lib/workspaceFs.ts:14`, `app/src/lib/shell/files/components/FilesPanel.svelte:891` | `{ path: string }` (remote only; Mac uses plugin-fs `readDir`) | `{ name, isDirectory, isFile, isSymlink }[]` | none found as a Tauri command; remote arm only: `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/workspace.rs:99` | 0 of 2 |
| `workspace_rename` | `app/src/lib/workspaceFs.ts:17`, `app/src/lib/shell/files/components/FilesPanel.svelte:861`, `app/src/lib/shell/files/components/FilesPanel.svelte:912` | `{ source: string, target: string }` (remote only; Mac uses plugin-fs `rename`) | void | none found as a Tauri command; remote arm only: `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/workspace.rs:96` | 0 of 2 |
| `workspace_copy_file` | `app/src/lib/workspaceFs.ts:20`, `app/src/lib/shell/files/components/FilesPanel.svelte:896`, `app/src/lib/shell/files/components/FilesPanel.svelte:918` | `{ source: string, target: string }` (remote only; Mac uses plugin-fs `copyFile`) | void | none found as a Tauri command; remote arm only: `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/workspace.rs:97` | 0 of 2 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| `source_scan_progress` | event | `{ scanId: string, visitedEntries: number, matchedFiles: number }` | `app/src/lib/tauriSource.ts:960` (listener set up by `subscribeToSourceScanProgress` `tauriSource.ts:937`; nothing in the UI calls that function) | `tauri-svelte-preview/src-tauri/src/files/discovery/mod.rs:219` (constant `files/discovery/mod.rs:38`; emits only for calls that sent a `scanId`) |

### Defined but not called by the UI
- None for the commands above. (`read_native_csharp_file` `…/files/commands.rs:170` is called, under Code intelligence.)

## Terminal
Embedded terminal sessions (start, type into, resize, close, replay scrollback, live output stream), plus opening an external terminal app and mirroring an agent's tool terminal.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `start_terminal_session` | `app/src/lib/tauriSource.ts:999`, `app/src/lib/shell/terminal/terminalService.ts:306`, `app/src/lib/shell/terminal/WorkspaceTerminal.svelte:187` | `{ request: { cwd: string, shell?: string?, cols?: number?, rows?: number?, ownedId?: string?, command?: string? }, requestId: number }` (`cwd` may be machine-qualified: the Rust command itself splits it, it is not routed through `remote_workspace`) | `TerminalSessionInfo` = `{ sessionId, cwd, shell, cols, rows, pid: number?, startedAt, exited, exitCode: number?, signal: string?, kind?, ownedId?, toolTerminalIdentity? }` | `tauri-svelte-preview/src-tauri/src/terminal/commands.rs:13` (remote split at `terminal/commands.rs:20`) | 1 of 14 |
| `list_terminal_sessions` | `app/src/lib/tauriSource.ts:1014`, `app/src/lib/shell/terminal/terminalService.ts:334`, `app/src/lib/shell/stacks/runOutputService.ts:74` | none | `TerminalSessionInfo[]` | `tauri-svelte-preview/src-tauri/src/terminal/commands.rs:33` | 2 of 19 |
| `read_terminal_session_scrollback` | `app/src/lib/tauriSource.ts:1211`, `app/src/lib/shell/terminal/terminalService.ts:329`, `app/src/lib/shell/terminal/WorkspaceTerminal.svelte:128` | `{ sessionId: string, maxBytes?: number }` | `{ data: string, generation: number, sequence: number }?` | `tauri-svelte-preview/src-tauri/src/terminal/commands.rs:40` | 2 of 15 |
| `write_terminal_session` | `app/src/lib/tauriSource.ts:1226`, `app/src/lib/shell/terminal/terminalService.ts:311`, `app/src/lib/shell/conversation/conversationService.ts:1602` | `{ sessionId: string, data: string }` | `boolean` | `tauri-svelte-preview/src-tauri/src/terminal/commands.rs:53` | 0 of 10 |
| `resize_terminal_session` | `app/src/lib/tauriSource.ts:1239`, `app/src/lib/shell/terminal/terminalService.ts:316`, `app/src/lib/shell/terminal/WorkspaceTerminal.svelte:71` | `{ sessionId: string, cols: number, rows: number }` | `boolean` | `tauri-svelte-preview/src-tauri/src/terminal/commands.rs:66` | 0 of 12 |
| `close_terminal_session` | `app/src/lib/tauriSource.ts:1248`, `app/src/lib/shell/terminal/terminalService.ts:321`, `app/src/lib/shell/terminal/WorkspaceTerminal.svelte:181` | `{ sessionId: string }` | `boolean` | `tauri-svelte-preview/src-tauri/src/terminal/commands.rs:80` | 1 of 14 |
| `register_terminal_output_stream` | `app/src/lib/tauriSource.ts:1290` (command name passed in at `tauriSource.ts:1354`; used by `listenToTerminalOutput` `tauriSource.ts:1362` <- `app/src/lib/shell/terminal/terminalService.ts:101`) | `{ registrationId: string (uuid), channel: Channel<wire envelope> }` | void; frames arrive on the channel (see below) | `tauri-svelte-preview/src-tauri/src/projection_streams.rs:550` | 0 of 2 |
| `acknowledge_terminal_output_stream` | `app/src/lib/tauriSource.ts:1265` (name passed in at `tauriSource.ts:1355`) | `{ registrationId: string, frames: number (always 1), bytes: number }` sent after every received frame | void | `tauri-svelte-preview/src-tauri/src/projection_streams.rs:559` | 0 of 2 |
| `unregister_terminal_output_stream` | `app/src/lib/tauriSource.ts:1297` (name passed in at `tauriSource.ts:1356`) | `{ registrationId: string }` | void; errors swallowed | `tauri-svelte-preview/src-tauri/src/projection_streams.rs:569` | 0 of 1 |
| `open_terminal_path` | `app/src/lib/tauriSource.ts:1562` (wrapper never called) | `{ path: string, terminal: string? (default 'Warp') }` | void | `tauri-svelte-preview/src-tauri/src/files/commands.rs:238` | 0 of 4 |
| `open_terminal_command` | `app/src/lib/tauriSource.ts:1579` (wrapper never called) | `{ path: string, command: string, terminal: string? (default 'Warp') }` | void | `tauri-svelte-preview/src-tauri/src/files/commands.rs:250` | 0 of 4 |
| `start_agent_conversation_terminal_projection` | `app/src/lib/shell/conversation/conversationService.ts:636` | `{ request: { ownedId: string, provider: AgentConversationProvider, nativeSessionId: string } }` | `{}` (Rust: `TerminalProjectionRegistration`); on failure the UI forgets it and retries on next activation | `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:1171` | 1 of 3 |
| `stop_agent_conversation_terminal_projection` | `app/src/lib/shell/conversation/conversationService.ts:621` | `{ ownedId: string }` | `boolean` | `tauri-svelte-preview/src-tauri/src/agent_conversation/mod.rs:1181` | 0 of 2 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| terminal output stream (`channel` arg of `register_terminal_output_stream`) | channel | wire frame `{ session: string, generation: number, sequence: number, bytes: number, kind:'terminal_output', resync: boolean, chunk: { sessionId, data: string, terminated: boolean, exitCode: number?, signal: string? } or null }`; `resync` or null chunk triggers a resync callback; UI maps it to `{ sessionId, generation, sequence, byteLength, kind, chunk }` | `app/src/lib/tauriSource.ts:1271` (channel built in `registerProjectionStream` `tauriSource.ts:1251`), consumed in `app/src/lib/shell/terminal/terminalService.ts:675` | local shells: `tauri-svelte-preview/src-tauri/src/terminal/mod.rs:519` and `terminal/mod.rs:586`; remote shells re-published under the desktop's id at `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:1353`; publisher `tauri-svelte-preview/src-tauri/src/projection_streams.rs:438` |

### Defined but not called by the UI
- None in terminal code. (`open_terminal_path` and `open_terminal_command` have TS wrappers that nothing calls, noted above.)

## Git
Source-control actions on a project's repository: status, diffs, stage/unstage, commit, amend, branches, stashes, discard, fetch/pull/push/publish, history.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `project_git_status` | `app/src/lib/tauriSource.ts:1612`, `app/src/lib/shell/git/gitService.ts:133` | `{ root: string }` | `ProjectGitStatus` = `{ branch: string?, ahead, behind, hasUpstream, files: { relativePath, indexStatus, worktreeStatus, status, badge }[] (max 1000), totalFiles?, additions?, deletions? }` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:23`; remote arm `…/remote/workspace.rs:47` | 0 of 9 |
| `read_source_git_diff` | `app/src/lib/tauriSource.ts:1625`, `app/src/lib/shell/git/gitService.ts:173` | `{ root: string, path: string (absolute path on disk) }` (+ AbortSignal) | `SourceGitDiff` = `{ relativePath, status, diff: string, isBinary, originalContent: string?, modifiedContent: string? }` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:30`; remote arm `…/remote/workspace.rs:48` | 0 of 8 |
| `read_git_branch_diff` | `app/src/lib/shell/git/gitBackendExtra.ts:268` | `{ root: string }` | `{ base: string, diff: string }` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:42`; remote arm `…/remote/workspace.rs:49` | 0 of 6 |
| `stage_git_paths` | `app/src/lib/tauriSource.ts:1637`, `app/src/lib/shell/git/gitService.ts:181` | `{ root: string, paths: string[] }` | `GitActionResult` = `{ message: string, status: ProjectGitStatus }` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:49`; remote arm `…/remote/workspace.rs:50` | 0 of 7 |
| `unstage_git_paths` | `app/src/lib/tauriSource.ts:1649`, `app/src/lib/shell/git/gitService.ts:185` | `{ root: string, paths: string[] }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:59`; remote arm `…/remote/workspace.rs:51` | 0 of 5 |
| `commit_git_repository` | `app/src/lib/tauriSource.ts:1661`, `app/src/lib/shell/git/gitService.ts:189` | `{ root: string, message: string }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:69`; remote arm `…/remote/workspace.rs:52` | 0 of 7 |
| `amend_git_commit` | `app/src/lib/tauriSource.ts:1824`, `app/src/lib/shell/git/gitService.ts:169` | `{ root: string, message: string }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:599`; remote arm `…/remote/workspace.rs:46` | 0 of 5 |
| `fetch_git_repository` | `app/src/lib/tauriSource.ts:1672`, `app/src/lib/shell/git/gitService.ts:193` | `{ root: string }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:81`; remote arm `…/remote/workspace.rs:53` | 0 of 8 |
| `pull_git_repository` | `app/src/lib/tauriSource.ts:1683`, `app/src/lib/shell/git/gitService.ts:197` | `{ root: string }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:88`; remote arm `…/remote/workspace.rs:54` | 0 of 8 |
| `push_git_repository` | `app/src/lib/tauriSource.ts:1694`, `app/src/lib/shell/git/gitService.ts:201` | `{ root: string }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:95`; remote arm `…/remote/workspace.rs:55` | 0 of 8 |
| `publish_git_repository` | `app/src/lib/tauriSource.ts:1704`, `app/src/lib/shell/git/gitService.ts:209` | `{ root: string, expectedBranch: string, remote: string }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:102`; remote arm `…/remote/workspace.rs:62` | 0 of 4 |
| `add_git_remote` | `app/src/lib/tauriSource.ts:1714`, `app/src/lib/shell/git/gitService.ts:205` | `{ root: string, name: string, url: string }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:109`; remote arm `…/remote/workspace.rs:61` | 0 of 3 |
| `workspace_git_init` | `app/src/lib/tauriSource.ts:1753`, `app/src/lib/shell/git/gitService.ts:213` | `{ path: string }` (+ AbortSignal; folder may be machine-qualified) | void (UI types it `null`) | `tauri-svelte-preview/src-tauri/src/project_folders.rs:294`; remote arm `…/remote/workspace.rs:95` | 0 of 3 |
| `read_git_commit_history` | `app/src/lib/tauriSource.ts:1949`, `app/src/lib/shell/git/gitService.ts:177` | `{ root: string, cursor: string?, relativePath: string? }` | `GitHistoryPage` = `{ root, relativePath: string?, commits: { shortSha, sha, subject, author, committedAt, refs, parentShas: string[], parentCount, taskID: string?, taskSource: string? }[], nextCursor: string?, complete: boolean }` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:116`; remote arm `…/remote/workspace.rs:63` | 0 of 5 |
| `read_git_commit_files` | `app/src/lib/shell/git/gitBackendExtra.ts:156` | `{ root: string, sha: string }` | `{ relativePath, status, badge }[]` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:129`; remote arm `…/remote/workspace.rs:64` | 0 of 5 |
| `read_git_commit_file_diff` | `app/src/lib/shell/git/gitBackendExtra.ts:171` | `{ root: string, sha: string, relativePath: string (relative to the repository) }` (+ AbortSignal) | `SourceGitDiff` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:141`; remote arm `…/remote/workspace.rs:65` | 0 of 5 |
| `list_git_branches` | `app/src/lib/tauriSource.ts:1747`, `app/src/lib/shell/git/gitService.ts:145`, `app/src/lib/shell/git/components/pr/PullRequestPanel.svelte:104` | `{ root: string }` (+ AbortSignal) | `GitBranchList` = `{ current: string, branches: { name, isCurrent, upstream, worktreePath, subject }[], remotes: string[] }` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:541`; remote arm `…/remote/workspace.rs:40` | 0 of 7 |
| `create_git_branch` | `app/src/lib/tauriSource.ts:1766`, `app/src/lib/shell/git/gitService.ts:149` | `{ root: string, name: string, checkout: boolean }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:548`; remote arm `…/remote/workspace.rs:41` | 0 of 5 |
| `switch_git_branch` | `app/src/lib/tauriSource.ts:1778`, `app/src/lib/shell/git/gitService.ts:153` | `{ root: string, name: string }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:561`; remote arm `…/remote/workspace.rs:42` | 0 of 7 |
| `stash_git_changes` | `app/src/lib/tauriSource.ts:1791`, `app/src/lib/shell/git/gitService.ts:157` | `{ root: string, includeUntracked: boolean, message: string }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:568`; remote arm `…/remote/workspace.rs:43` | 0 of 5 |
| `pop_git_stash` | `app/src/lib/tauriSource.ts:1803`, `app/src/lib/shell/git/gitService.ts:161` | `{ root: string, index: number? }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:585`; remote arm `…/remote/workspace.rs:44` | 0 of 5 |
| `list_git_stashes` | `app/src/lib/tauriSource.ts:1812`, `app/src/lib/shell/git/gitService.ts:165` | `{ root: string }` | `{ index: number, label: string, description: string }[]` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:592`; remote arm `…/remote/workspace.rs:45` | 0 of 4 |
| `discard_git_paths` | `app/src/lib/tauriSource.ts:1726`, `app/src/lib/shell/git/gitService.ts:137` | `{ root: string, paths: string[] }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:519`; remote arm `…/remote/workspace.rs:38` | 0 of 5 |
| `discard_all_git_changes` | `app/src/lib/tauriSource.ts:1738`, `app/src/lib/shell/git/gitService.ts:141` | `{ root: string, includeUntracked: boolean }` | `GitActionResult` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:529`; remote arm `…/remote/workspace.rs:39` | 0 of 4 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| none found | | | | |

### Defined but not called by the UI
- None found in `git/commands.rs` or `git/workspace.rs` (every command there has a UI caller above, or is listed under Worktrees / PRs).

## Browser
The in-app web browser: native webview tabs the UI creates, positions, navigates, inspects (element picker, screenshot) and closes. Every command is sent as `invoke(name, { input })` through `invokeBrowserCommandFromTauri` (`app/src/lib/tauriSource.ts:1592`, which calls `workspaceInvoke`), and commands are queued one at a time in the UI (`browserBackend.ts` `enqueue`). Shared shapes: Target = `{ workspaceId: string, tabId: string, generation: number }`; Bounds = `{ x, y, width, height }`; Viewport = `{ preset, width: number|null, height: number|null }`.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `create_browser_tab` | `app/src/lib/shell/browser/browserBackend.ts:358` | `{ input: { workspaceId, tabId, generation, url: string, bounds?: Bounds, viewport?: Viewport, profileId?: string\|null } }` | `{ tabId: string, generation: number }` | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1398` | 1 of 4 |
| `set_browser_tab_bounds` | `app/src/lib/shell/browser/browserBackend.ts:364` | `{ input: Target & { bounds: Bounds } }` | nothing (void) | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1411` | 1 of 3 |
| `set_browser_tab_viewport` | `app/src/lib/shell/browser/browserBackend.ts:370` | `{ input: Target & { viewport: Viewport } }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1426` | 1 of 3 |
| `show_browser_tab` | `app/src/lib/shell/browser/browserBackend.ts:376` | `{ input: Target }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1434` | 1 of 3 |
| `hide_browser_workspace` | `app/src/lib/shell/browser/browserBackend.ts:382` | `{ input: { workspaceId: string } }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1442` | 1 of 3 |
| `navigate_browser_tab` | `app/src/lib/shell/browser/browserBackend.ts:388` | `{ input: Target & { url: string } }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1450` | 1 of 3 |
| `reload_browser_tab` | `app/src/lib/shell/browser/browserBackend.ts:394` | `{ input: Target }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1458` | 1 of 3 |
| `go_back_browser_tab` | `app/src/lib/shell/browser/browserBackend.ts:400` | `{ input: Target }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1466` | 1 of 3 |
| `go_forward_browser_tab` | `app/src/lib/shell/browser/browserBackend.ts:406` | `{ input: Target }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1474` | 1 of 3 |
| `close_browser_tab` | `app/src/lib/shell/browser/browserBackend.ts:412` | `{ input: Target }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1482` | 1 of 3 |
| `arm_browser_element_picker` | `app/src/lib/shell/browser/browserBackend.ts:418` | `{ input: Target & { mode: 'grab'\|'annotation' } }` | void (the pick arrives later on the `browser-element-selected` event) | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1498` | 1 of 3 |
| `cancel_browser_element_picker` | `app/src/lib/shell/browser/browserBackend.ts:424` | `{ input: Target }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1506` | 1 of 3 |
| `inspect_browser_rect` | `app/src/lib/shell/browser/browserBackend.ts:430` | `{ input: Target & { rect: {x,y,width,height} } }` | `{ selector, role, accessibleName, textSnippet, rect, classes: string[], classCount } \| null` (all but classes/classCount nullable) | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1514` | 0 of 2 |
| `capture_browser_viewport` | `app/src/lib/shell/browser/browserBackend.ts:436` | `{ input: Target }` | `{ mimeType: string, bytes: number[], width, height, sourceHash: string }` | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1522` | 1 of 3 |
| `open_browser_tab_devtools` | `app/src/lib/shell/browser/browserBackend.ts:442` | `{ input: Target }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1530` | 1 of 3 |
| `open_browser_tab_external` | `app/src/lib/shell/browser/browserBackend.ts:448` | `{ input: Target }` | void | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1538` | 1 of 4 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| `browser-tab-navigation` | event | `{ workspaceId, tabId, generation, url, title, canGoBack: boolean, canGoForward: boolean }` | `app/src/lib/shell/browser/browserBackend.ts:111` | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:380` (`TauriBrowserEventSink::navigation`, fn at :379) |
| `browser-element-selected` | event | `{ workspaceId, tabId, generation, status: 'selected'\|'unavailable', reason: string\|null, url, title, selector, accessibleName, textSnippet (all nullable), rect: Rect\|null, classes: string[], classCount: number, sourceHash: string }` | `app/src/lib/shell/browser/browserElementEvents.ts:87` | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:388` (`element_selected`); the "unavailable" case comes from `emit_picker_unavailable` at `browser/mod.rs:1241` |
| `browser-tab-load` | event | not listened by the UI | none | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:384` |
| `browser-tab-closed` | event | not listened by the UI | none | `tauri-svelte-preview/src-tauri/src/browser/mod.rs:392` |

### Defined but not called by the UI
- `clear_browser_workspace_data` — `tauri-svelte-preview/src-tauri/src/browser/mod.rs:1490`

## Code intelligence
Go-to-definition, references, hover, completion, diagnostics, formatting, rename and related language-server features for open source files, plus the per-project and global switches for language servers and the Problems panel's whole-project diagnostics.

All `find_source_lsp_*`, `format_source_with_lsp`, `rename_source_with_lsp` and `read_source_lsp_diagnostics` calls send `{ preview: SourcePreview, request: <lookup> }` (no top-level path key, so never remote-routed). Lookup request = `SourceLspLookupRequest` `{ root, line, column, limit?: number }` unless the row says otherwise. `SourceDefinitionTarget` = `SourceRecord + { symbolName, kind, line, column, detail }`; `SourceReferenceTarget` = `SourceRecord + { symbolName, line, column, excerpt }`.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `find_source_lsp_definitions` | `app/src/lib/tauriSource.ts:3108`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:355` | `{ preview, request }` | `SourceDefinitionTarget[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:4` | 0 of 3 |
| `find_source_lsp_completions` | `app/src/lib/tauriSource.ts:3123`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:1127` | `{ preview, request }` | `{ label, kind, detail, insertText }[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:16` | 0 of 3 |
| `find_source_lsp_implementations` | `app/src/lib/tauriSource.ts:3153` (wrapper never called) | `{ preview, request }` | `SourceDefinitionTarget[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:28` | 0 of 3 |
| `find_source_lsp_type_definitions` | `app/src/lib/tauriSource.ts:3168` (wrapper never called) | `{ preview, request }` | `SourceDefinitionTarget[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:40` | 0 of 3 |
| `find_source_lsp_document_highlights` | `app/src/lib/tauriSource.ts:3183`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:1147` | `{ preview, request }` | `{ startLine, startColumn, endLine, endColumn, kind:'text'/'read'/'write' }[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:52` | 0 of 3 |
| `find_source_lsp_signature_help` | `app/src/lib/tauriSource.ts:3244`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:1167` | `{ preview, request }` | `{ signatures: { label, documentation, parameters: { label, documentation }[] }[], activeSignature, activeParameter }?` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:66` | 0 of 3 |
| `find_source_lsp_inlay_hints` | `app/src/lib/tauriSource.ts:3260`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:1182` | `{ preview, request }` | `{ label, tooltip, kind:'type'/'parameter'/'other', line, column, paddingLeft, paddingRight }[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:78` | 0 of 3 |
| `find_source_lsp_semantic_tokens` | `app/src/lib/tauriSource.ts:3276`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:1203` | `{ preview, request }` | `{ tokenType, line, startColumn, length }[]` (token types from a fixed legend of 17 names) | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:90` | 0 of 3 |
| `find_source_lsp_workspace_symbols` | `app/src/lib/tauriSource.ts:3323` (wrapper never called) | `{ preview, request: { root, query: string, limit?: number } }` | `SourceWorkspaceSymbol[]` = `SourceRecord + { symbolName, kind, line, column, detail, containerName: string? }` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:102` | 0 of 3 |
| `format_source_with_lsp` | `app/src/lib/tauriSource.ts:3198` (wrapper never called) | `{ preview, request }` | `{ startLine, startColumn, endLine, endColumn, newText }[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:114` | 0 of 3 |
| `rename_source_with_lsp` | `app/src/lib/tauriSource.ts:3213` (wrapper never called) | `{ preview, request: { root, line, column, newName: string } }` | `{ files: { path, relativePath, edits: SourceTextEdit[] }[] }` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:126` | 0 of 3 |
| `find_source_lsp_code_actions` | `app/src/lib/tauriSource.ts:3228`, `app/src/lib/shell/files/components/EditorPanel.svelte:1010` | `{ preview, request: { root, limit?, startLine, startColumn, endLine, endColumn, diagnostics: { severity, message, startLine, startColumn, endLine, endColumn, source? }[] } }` | `{ title, kind, isPreferred, disabledReason: string?, files: { path, relativePath, edits }[] }[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:138` | 0 of 3 |
| `find_source_lsp_references` | `app/src/lib/tauriSource.ts:3138`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:631` | `{ preview, request }` | `SourceReferenceTarget[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:150` | 0 of 3 |
| `find_source_lsp_hover` | `app/src/lib/tauriSource.ts:3292`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:1110` | `{ preview, request }` | `{ contents: string[] }?` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:162` | 0 of 3 |
| `find_source_lsp_document_symbols` | `app/src/lib/shell/files/editor/sourceIntelligence.ts:1030` (direct `@tauri-apps/api/core` invoke; only after `read_backend_capabilities` lists `lspDocumentSymbols`) | `{ root: string, language: string, path: string }` | `{ name, kind, line (0-based), character (0-based) }[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:181` | 0 of 4 |
| `find_source_lsp_symbols` | `app/src/lib/tauriSource.ts:3307` (wrapper never called) | `{ preview, request }` | `{ name, kind, line, column, detail }[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:215` | 0 of 3 |
| `read_source_lsp_diagnostics` | `app/src/lib/tauriSource.ts:3338`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:375`, `app/src/lib/shell/problems/problemsService.ts:143` | `{ preview, request }` | `SourceDiagnostic[]` = `{ severity:'error'/'warning'/'info'/'hint', message, line, column, source? }` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:227` | 0 of 3 |
| `list_source_lsp_diagnostics_for_root` | `app/src/lib/shell/problems/problemsBackend.ts:66` (direct core invoke), `app/src/lib/shell/problems/problemsService.ts:96` | `{ root: string }` | `(SourceDiagnostic + { path?: string? })[]`; the UI treats an "unknown command" error as "app too old" and falls back to per-file diagnostics | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:239` | 0 of 5 |
| `read_source_lsp_status` | `app/src/lib/tauriSource.ts:3066`, `app/src/lib/shell/files/components/EditorPanel.svelte:505`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:782` | `{ root: string, language: string }` | `SourceLspStatus` = `{ language, languageID, available, serverName, command, args: string[], reason: string? }` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:252` | 0 of 3 |
| `list_source_lsp_statuses` | `app/src/lib/tauriSource.ts:3080` (wrapper never called) | `{ root: string }` | `SourceLspStatus[]` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:264` | 0 of 4 |
| `warm_source_lsp_for_root` | `app/src/lib/tauriSource.ts:3096`, `app/src/lib/shell/files/components/EditorPanel.svelte:787` | `{ root: string }` | `number` (running servers re-pointed) | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:279` | 0 of 3 |
| `ensure_native_csharp_language_client` | `app/src/lib/tauriSource.ts:1435`, `app/src/lib/shell/files/editor/csharpLanguageClient.ts:95` | `{ root: string }` | `{ wsUrl: string, root: string }` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:293` | 0 of 3 |
| `read_native_csharp_file` | `app/src/lib/tauriSource.ts:1427`, `app/src/lib/shell/files/editor/csharpFileSystem.ts:53` | `{ root: string, path: string }` | `SourcePreview` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:170` | 0 of 3 |
| `mark_native_csharp_language_client_ready` | `app/src/lib/tauriSource.ts:1486`, `app/src/lib/shell/files/editor/csharpLanguageClient.ts:177` | `{ root: string }` | void | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:310` | 0 of 3 |
| `read_workspace_language_intelligence` | `app/src/lib/tauriSource.ts:1460` (wrapper never called) | `{ root: string }` | `WorkspaceLanguageIntelligence` = `{ root, enabled, runningServers, serverPids: number[], stoppedServers, message }` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:393` | 0 of 4 |
| `set_workspace_language_intelligence` | `app/src/lib/tauriSource.ts:1476`, `app/src/lib/shell/files/components/EditorPanel.svelte:725` | `{ root: string, enabled: boolean, language: string? }` | `WorkspaceLanguageIntelligence` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:424` | 0 of 4 |
| `set_language_server_enabled` | `app/src/lib/shell/files/editor/sourceIntelligence.ts:1353` (direct core invoke) | `{ language: 'csharp'/'typescript'/'rust', enabled: boolean }` | `{ enabled: boolean, stoppedServers: number, message: string }`; any error is read as "not supported" | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:473` | 0 of 5 |
| `set_language_servers_enabled` | `app/src/lib/shell/files/editor/sourceIntelligence.ts:1384` (direct core invoke; only if capability `languageServersToggle` is listed) | `{ enabled: boolean }` | `{ enabled, stoppedServers, message }` | `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:359` | 0 of 6 |
| `find_source_definitions_in_root` | `app/src/lib/tauriSource.ts:3050`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:397` | `{ root: string, symbolName: string, limit: number (default 20) }` | `SourceDefinitionTarget[]` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:322`; remote arm `…/remote/workspace.rs:88` | 1 of 5 |
| `find_source_references_in_root` | `app/src/lib/tauriSource.ts:3374`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:416`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:446` | `{ root: string, symbolName: string, limit: number (default 50) }` | `SourceReferenceTarget[]` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:350`; remote arm `…/remote/workspace.rs:89` | 1 of 5 |
| `count_source_references` | `app/src/lib/tauriSource.ts:3405`, `app/src/lib/shell/files/editor/sourceIntelligence.ts:490` | `{ root: string, symbolNames: string[], deadlineMs: number? }` | `{ counts: Record<string, number>, approximate: boolean, scannedFiles, elapsedMs }` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:365`; no remote arm | 0 of 5 |
| `find_source_definitions` | `app/src/lib/tauriSource.ts:3035` (wrapper never called) | `{ records: SourceRecord[], symbolName: string, limit: number (default 20) }` | `SourceDefinitionTarget[]` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:309` | 0 of 3 |
| `find_source_references` | `app/src/lib/tauriSource.ts:3358` (wrapper never called) | `{ records: SourceRecord[], symbolName: string, limit: number (default 50) }` | `SourceReferenceTarget[]` | `tauri-svelte-preview/src-tauri/src/files/commands.rs:337` | 0 of 3 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| `source-lsp-status-changed` | event | `{ root: string, language: string (server's language id, e.g. csharp/typescript/rust/svelte), state: 'not-running'/'starting'/'indexing'/'ready'/'disabled', detail: string? }`; only listened to if capability `lspStatusEvents` is listed | `app/src/lib/shell/files/editor/sourceIntelligence.ts:820` | `tauri-svelte-preview/src-tauri/src/main.rs:3561` (constant `main.rs:559`; payload struct `SourceLspStatusChange` `tauri-svelte-preview/src-tauri/src/files/lsp.rs:401`) |

### Defined but not called by the UI
- `read_source_lsp_log` — `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:200` (no TS wrapper; the UI's log button uses `read_language_server_log` instead, listed under Unassigned)
- `set_csharp_language_server_enabled` — `tauri-svelte-preview/src-tauri/src/files/lsp_commands.rs:324` (UI uses `set_language_server_enabled` with `language:'csharp'` instead)

## Worktrees
Listing a repository's git worktrees and each repository's state, removing or archiving a worktree.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `list_project_worktrees` | `app/src/lib/tauriSource.ts:1960`, `app/src/lib/shell/worktrees/worktreeManagerService.ts:107` | `{ root: string }` | `ProjectWorktree[]` = `{ repo, path, branch, taskID: string?, isDirty, hasUnmergedCommits, isPrunable?, prunableReason?: string?, isLocked?, lockedReason?: string?, lastActivity: string?, deleteEligibility: string }` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:154`; remote arm `…/remote/workspace.rs:66` | 0 of 5 |
| `remove_project_worktree` | `app/src/lib/shell/worktrees/worktreesBackend.ts:92` (direct core invoke, never remote-routed; callers `worktreeManagerService.ts:168`, `:199`, `:237`); `app/src/lib/tauriSource.ts:1972` (wrapper without `force`, never called) | `{ root: string, path: string, force?: true }` (`force` is left off the wire unless true) | `ProjectWorktreeActionResult` = `{ message: string, worktrees: ProjectWorktree[] }` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:178`; remote arm `…/remote/workspace.rs:68` | 0 of 8 |
| `archive_project_worktree` | `app/src/lib/tauriSource.ts:1984`, `app/src/lib/shell/worktrees/worktreeManagerService.ts:255` | `{ root: string, path: string }` | `ProjectWorktreeArchiveResult` = `ProjectWorktreeActionResult + { archivePath: string }` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:192`; remote arm `…/remote/workspace.rs:69` | 0 of 5 |
| `list_git_repository_summaries` | `app/src/lib/tauriSource.ts:1995`, `app/src/lib/shell/worktrees/worktreeManagerService.ts:123` | `{ projects: { id, name, path }[] }` | `GitRepositorySummary[]` = `{ projectID, projectName, repo, path, rootLabel, branch, taskID: string?, isWorktree, isDirty, stagedCount, unstagedCount, untrackedCount, dirtyCount, ahead, behind, hasUpstream, lastCommitSha: string?, lastCommitSubject: string?, lastCommitAt: string?, dirtySinceEpochMs: number?, dirtyStatusFingerprint, error: string? }` | `tauri-svelte-preview/src-tauri/src/git/commands.rs:204`; no remote arm | 0 of 3 |
| `list_repository_checkouts` | `app/src/lib/tauriSource.ts:2063`, `app/src/lib/shell/files/components/FilesPanel.svelte:370`, `app/src/lib/shell/history/components/HistoryPanel.svelte:210` | `{ roots: string[] }` (+ AbortSignal) | `Record<string, { path, branch, isMain }[]>` keyed by the root asked about; deleted checkouts left out | `tauri-svelte-preview/src-tauri/src/git/commands.rs:161`; remote arm `…/remote/workspace.rs:67` | 1 of 5 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| none found | | | | |

### Defined but not called by the UI
- None found.

## PRs
Writing a commit message or pull-request description with the agent, opening a pull request on GitHub, checking its status, and browsing, reviewing, commenting on and merging hosted pull requests.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `generate_commit_message` | `app/src/lib/tauriSource.ts:1899`, `app/src/lib/shell/git/components/SourceControlPanes.svelte:113` | `{ request: { root: string, ownedId: string, generation: number } }` | `string` (commit message) | `tauri-svelte-preview/src-tauri/src/git/pull_requests.rs:63` | 0 of 4 |
| `read_pull_request_context` | `app/src/lib/tauriSource.ts:1907`, `app/src/lib/shell/git/components/pr/PullRequestPanel.svelte:85` | `{ root: string }` | `{ branch, base, commits: string, diff: string }` | `tauri-svelte-preview/src-tauri/src/git/pull_requests.rs:98` | 0 of 3 |
| `generate_pull_request_details` | `app/src/lib/tauriSource.ts:1915`, `app/src/lib/shell/git/components/pr/PullRequestPanel.svelte:122` | `{ request: { root: string, ownedId: string, generation: number } }` | `{ title: string, description: string }` | `tauri-svelte-preview/src-tauri/src/git/pull_requests.rs:107` | 0 of 4 |
| `create_pull_request` | `app/src/lib/tauriSource.ts:1927`, `app/src/lib/shell/git/components/pr/PullRequestPanel.svelte:138` | `{ root: string, title: string, description: string, base: string, draft: boolean }` (fields sent flat) | `{ number: number, url: string }` | `tauri-svelte-preview/src-tauri/src/git/pull_requests.rs:137` | 0 of 3 |
| `read_pull_request_status` | `app/src/lib/tauriSource.ts:1936`, `app/src/lib/shell/git/components/pr/PullRequestPanel.svelte:156` | `{ root: string, branch: string }` | `{ number, url, state, checks:'none'/'pending'/'passing'/'failing', checkSummary }` | `tauri-svelte-preview/src-tauri/src/git/pull_requests.rs:152` | 0 of 3 |
| `list_open_pull_requests` | `app/src/lib/tauriSource.ts:1835`, `app/src/lib/shell/git/components/pr/PullRequestList.svelte:53` | `{ root: string }` | `{ number, title, url, state, headBranch, isDraft, checks, checkSummary }[]` | `tauri-svelte-preview/src-tauri/src/git/workspace.rs:611`; no remote arm | 0 of 4 |
| `list_github_pull_requests` | `app/src/lib/tauriSource.ts:1844`, `app/src/lib/shell/git/github/PullRequestWorkspace.svelte:266` | `{ query: { roots: string[], projectFilter: string?, search: string?, cursor: string?, pageSize: number } }` (+ AbortSignal) | `{ items: { repository, localRoot, number, title, url, isDraft, updatedAt, state, author, headBranch, baseBranch }[], nextCursor: string?, totalCount }` | `tauri-svelte-preview/src-tauri/src/git/github.rs:202`; remote arm `…/remote/workspace.rs:72` | 0 of 4 |
| `read_github_pull_request` | `app/src/lib/tauriSource.ts:1853`, `app/src/lib/shell/git/github/PullRequestWorkspace.svelte:217` | `{ root: string, number: number }` | `GithubPullRequestDetail` = `{ repository, localRoot, number, title, body, url, author, headBranch, baseBranch, headSha, baseSha, state, isDraft, mergeable, reviewDecision, createdAt, mergedAt, mergedBy, closedAt, commits[], comments[], reviews[], reviewers: string[], checks[], files[] (path, previousPath?, status, additions, deletions, patch?), moreFiles }` | `tauri-svelte-preview/src-tauri/src/git/github.rs:211`; remote arm `…/remote/workspace.rs:73` | 0 of 5 |
| `read_github_pull_request_file` | `app/src/lib/tauriSource.ts:1861`, `app/src/lib/shell/git/github/PullRequestWorkspace.svelte:246` | `{ query: { root, path, previousPath: string?, status, baseSha, headSha } }` | `{ originalContent: string, modifiedContent: string }` | `tauri-svelte-preview/src-tauri/src/git/github.rs:218`; remote arm `…/remote/workspace.rs:74` | 0 of 4 |
| `submit_github_pull_request_review` | `app/src/lib/tauriSource.ts:1869`, `app/src/lib/shell/git/github/PullRequestWorkspace.svelte:508` | `{ submission: { root, number, expectedHeadSha, body, comments: { path, line, side:'LEFT'/'RIGHT', body }[] } }` | `string` (URL) | `tauri-svelte-preview/src-tauri/src/git/github.rs:225`; remote arm `…/remote/workspace.rs:75` | 0 of 5 |
| `reply_github_pull_request_comment` | `app/src/lib/tauriSource.ts:1883`, `app/src/lib/shell/git/github/PullRequestWorkspace.svelte:444` | `{ reply: { root, number, expectedHeadSha, commentId: number, body } }` | `string` (URL) | `tauri-svelte-preview/src-tauri/src/git/github.rs:232`; remote arm `…/remote/workspace.rs:76` | 0 of 5 |
| `comment_github_pull_request` | `app/src/lib/tauriSource.ts:1875`, `app/src/lib/shell/git/github/PullRequestWorkspace.svelte:486` | `{ comment: { root, number, body } }` | `string` (URL) | `tauri-svelte-preview/src-tauri/src/git/github.rs:239`; remote arm `…/remote/workspace.rs:77` | 0 of 4 |
| `merge_github_pull_request` | `app/src/lib/tauriSource.ts:1891`, `app/src/lib/shell/git/github/PullRequestWorkspace.svelte:545` | `{ request: { root, number, expectedHeadSha, expectedBaseSha, method:'merge'/'squash'/'rebase' } }` | `string` (URL) | `tauri-svelte-preview/src-tauri/src/git/github.rs:246`; remote arm `…/remote/workspace.rs:78` | 0 of 6 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| none found | | | | |

### Defined but not called by the UI
- None found.

## Providers and settings
Provider adapter and CLI version checks/installs (this Mac and saved machines), provider list per machine, app settings storage, the utility ("helper") model settings, backend capability flags, and the proof-mode badge.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `check_provider_clis` | `app/src/lib/shell/providers/providerCliUpdateService.svelte.ts:14` | `{ profileId: string\|null }` | `CliStatus[] { provider: 'codex'\|'claude', version: string\|null, error: string\|null }` | `RS/agent_conversation/providers/cli_updates.rs:144` | 1 of 2 |
| `update_provider_cli` | `app/src/lib/shell/providers/providerCliUpdateService.svelte.ts:26` | `{ provider, profileId: string\|null }` | `CliStatus` | `RS/agent_conversation/providers/cli_updates.rs:158` | 1 of 3 |
| `check_provider_updates` | `app/src/lib/shell/providers/providerUpdateService.svelte.ts:51` (command name picked by `profileId`) | no args | `ProviderUpdateStatus { target, updateAvailable, restartRequired, providers: { provider, currentVersion, availableVersion, updateAvailable }[] }` | `RS/agent_conversation/providers/updates.rs:88` | 0 of 3 |
| `install_provider_updates` | `app/src/lib/shell/providers/providerUpdateService.svelte.ts:85` | `{ provider }` | `ProviderUpdateStatus` | `RS/agent_conversation/providers/updates.rs:100` | 0 of 5 |
| `restart_for_provider_updates` | `app/src/lib/shell/providers/providerUpdateService.svelte.ts:111` | no args | void | `RS/agent_conversation/providers/updates.rs:147` | 0 of 3 |
| `check_remote_provider_updates` | `app/src/lib/shell/providers/providerUpdateService.svelte.ts:51`, `app/src/lib/shell/settings/MachineProviders.svelte:45` | `{ profileId }` | `ProviderUpdateStatus` | `RS/agent_conversation/remote/mod.rs:5452` (remote server arm `RemoteCommand::CheckProviderUpdates` `remote/server/mod.rs:313`) | 0 of 2 |
| `install_remote_provider_updates` | `app/src/lib/shell/providers/providerUpdateService.svelte.ts:85` | `{ provider, profileId }` | `ProviderUpdateStatus` | `RS/agent_conversation/remote/mod.rs:5463` | 0 of 5 |
| `restart_remote_for_provider_updates` | `app/src/lib/shell/providers/providerUpdateService.svelte.ts:111` | `{ profileId }` | void | `RS/agent_conversation/remote/mod.rs:5490` | 0 of 3 |
| `list_provider_descriptors` | `app/src/lib/shell/settings/MachineProviders.svelte:29` | no args | `ProviderSummary[]` (has `id`, `adapterVersion`; other fields unknown) | `RS/agent_conversation/providers/descriptors.rs:232` | 0 of 1 |
| `read_backend_capabilities` | `app/src/lib/shell/backendCapabilities.ts:32`, `app/src/lib/shell/processes/processBackend.ts:66`, `app/src/lib/shell/worktrees/worktreesBackend.ts:106` | no args | `string[]` (capability names) | `RS/commands/application.rs:11` | 0 of 2 |
| `read_proof_mode_label` | `app/src/routes/+layout.svelte:15` | no args | `string \| null` | answered inside the command guard, not a normal fn: `RS/proof_mode.rs:109` | 0 of 1 |
| `read_assembly_setting` | `app/src/lib/tauriSource.ts:2770` (users: `settings/settingsStore.svelte.ts:18`, `settings/HelperSessionSettings.svelte:21`) | `{ settingKey }` | `string \| null` (JSON text; UI parses) | `RS/agent_conversation/mod.rs:506` | 1 of 2 |
| `write_assembly_setting` | `app/src/lib/tauriSource.ts:2751` | `{ settingKey, valueJson: string }` (writes per key are queued in order) | void | `RS/agent_conversation/mod.rs:291` | 1 of 2 |
| `read_helper_settings` | `app/src/lib/tauriSource.ts:1160` (used by `settings/SettingsDialog.svelte`) | no args | `HelperSettingsView { vendor, model, route, hasKey, openaiModels, anthropicModels }` | `RS/helper/mod.rs:501` | 0 of 4 |
| `write_helper_settings` | `app/src/lib/tauriSource.ts:1168` | `{ settings: { vendor, model, route } }` | void | `RS/helper/mod.rs:520` | 0 of 2 |
| `set_helper_key` | `app/src/lib/tauriSource.ts:1175` | `{ vendor, key }` (empty key removes it) | void | `RS/helper/mod.rs:486` | 0 of 1 |
| `test_helper` | `app/src/lib/tauriSource.ts:1181` | no args | `HelperTestResult { ok, message }` | `RS/helper/mod.rs:529` | 0 of 3 |
| `run_helper_job` | `app/src/lib/tauriSource.ts:1199` | `{ job: 'title'\|'inspect'\|'review', input: string }` | `string` (rejects with a sentence when off) | `RS/helper/mod.rs:471` | 0 of 1 |
| `list_installed_font_families` | `app/src/lib/tauriSource.ts:760` (from `shell/settings/SettingsDialog.svelte:431`) | none | `{ families: string[], monospacedFamilies: string[] }` | `tauri-svelte-preview/src-tauri/src/commands/application.rs:16` | 1 of 1 |

(Per-machine adapter config check for the provider list uses `probe_agent_provider_config`, listed under Chat: `MachineProviders.svelte:62`.)

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| `provider-download-progress` | event | `{ provider, fileIndex, fileCount, downloaded, total: number\|null, profileId: string\|null }`; ignored unless provider and profile match the current install | `app/src/lib/shell/providers/providerUpdateService.svelte.ts:79` | `RS/agent_conversation/providers/updates.rs:111` (local), `RS/agent_conversation/remote/mod.rs:5479` (remote) |

### Defined but not called by the UI
- none found in this feature's Rust code.

## Resources and usage
Live process/resource sampling and disk-space reclaim, plus usage history (token and cost totals by provider, model, day) and current provider usage.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `read_resource_snapshot` | `app/src/lib/tauriSource.ts:1020` | none | `ResourceSnapshot` | `RS/resources.rs:289` | 2 of 4 |
| `cancel_resource_snapshot` | `app/src/lib/tauriSource.ts:1026` | none | void | `RS/resources.rs:309` | 1 of 3 |
| `read_resource_disk_scan` | `app/src/lib/tauriSource.ts:1036` | `{ roots, maxDepth, maxEntries }` | `DiskScanReport` | `RS/resources.rs:1252` | 1 of 3 |
| `cancel_resource_disk_scan` | `app/src/lib/tauriSource.ts:1042` | none | void | `RS/resources.rs:1292` | 0 of 2 |
| `stop_owned_resource` | `app/src/lib/tauriSource.ts:1050` | `{ request: ResourceStopRequest }` | `ResourceCommandReceipt` | `RS/resources.rs:1389` | 2 of 4 |
| `restart_language_server_root` | `app/src/lib/tauriSource.ts:1058` | `{ request: { root } }` | `ResourceUnavailable` | `RS/resources.rs:1684` | 0 of 2 |
| `apply_resource_memory_pressure` | `app/src/lib/tauriSource.ts:1066` | `{ level: string }` | `ResourceUnavailable` | `RS/resources.rs:1711` | 0 of 1 |
| `read_language_server_log` | `app/src/lib/tauriSource.ts:1074` | `{ request: { root } }` | `ResourceUnavailable` | `RS/resources.rs:1716` | 0 of 2 |
| `cleanup_workspace_disk_entry` | `app/src/lib/tauriSource.ts:1082` | `{ request: ResourceCleanupRequest }` | `ResourceCleanupReceipt` | `RS/resources.rs:1299` | 2 of 4 |
| `read_resource_sample` | `app/src/lib/shell/resources/resourceSampleBackend.ts:26` | none | `ResourceSample` | `RS/resources.rs:377` | 0 of 10 |
| `read_resource_totals` | `app/src/lib/shell/resources/resourceSampleBackend.ts:32` | none | `ResourceSampleTotals` | `RS/resources.rs:446` | 3 of 6 |
| `stop_resource_process_tree` | `app/src/lib/shell/resources/resourceSampleBackend.ts:44` | `{ request: ResourceStopRequestBody }` | `ResourceStopReceipt { action, rootPid, ownedId?, signal, followUpSignal, graceSeconds, stoppedPids, alreadyGonePids, message }` | `RS/resources.rs:1583` | 0 of 3 |
| `read_resource_disk_usage` | `app/src/lib/shell/resources/resourceSampleBackend.ts:50` | `{ request: { refresh: boolean } }` | `DiskUsageReport` | `RS/resources_disk.rs:394` | 0 of 1 |
| `reclaim_resource_disk_entry` | `app/src/lib/shell/resources/resourceSampleBackend.ts:59` | `{ request: DiskReclaimRequest }` | `DiskReclaimReceipt` | `RS/resources_disk.rs:430` | 0 of 1 |
| `read_current_provider_usage` | `app/src/lib/tauriSource.ts:1097` | `{ provider: string\|null, instanceId: string\|null }` | `ProviderUsageSnapshot` (UI normalizes) | `RS/usage_current.rs:41` | 2 of 4 |
| `read_usage_summary` | `app/src/lib/tauriSource.ts:1103` | `{ query: UsageHistoryQuery }` (`{ provider?, model?, projectId?, workflowId?, startMicros?, endMicros?, limit?, offset? }`) | `UsageSummary` | `RS/usage_history.rs:44` | 6 of 10 |
| `read_usage_breakdown` | `app/src/lib/tauriSource.ts:1109` | `{ query }` | `UsageBreakdownRow[]` | `RS/usage_history.rs:56` | 2 of 6 |
| `read_usage_provider_summary` | `app/src/lib/tauriSource.ts:1115` | `{ query }` | `UsageProviderSummaryRow[]` | `RS/usage_history.rs:81` | 4 of 7 |
| `read_usage_daily` | `app/src/lib/tauriSource.ts:1121` | `{ query }` | `UsageDailyRow[]` | `RS/usage_history.rs:93` | 1 of 2 |
| `read_usage_daily_totals` | `app/src/lib/tauriSource.ts:1127` | `{ query }` | `UsageDailyTotalsRow[]` | `RS/usage_history.rs:105` | 2 of 4 |
| `read_usage_token_breakdown` | `app/src/lib/shell/usage/usageBackend.ts:60` (via `readUsageQueryFromTauri`, invoke at `:56`) | `{ query }` | `UsageTokenBreakdown` | `RS/commands/usage.rs:4` | 1 of 3 |
| `read_usage_provider_daily_totals` | `app/src/lib/shell/usage/usageBackend.ts:64` | `{ query }` | `UsageProviderDailyTotalsRow[]` | `RS/commands/usage.rs:17` | 1 of 3 |
| `read_usage_cost_inputs` | `app/src/lib/shell/usage/usageBackend.ts:68` | `{ query }` | `UsageCostInputRow[]` | `RS/commands/usage.rs:30` | 1 of 3 |
| `refresh_usage_history` | `app/src/lib/tauriSource.ts:1133` | none | `number` (rows added) | `RS/usage_history.rs:117` | 2 of 3 |
| `set_active_source_root` | `app/src/lib/tauriSource.ts:1088` | `{ request: { root: string } }` | `string` (the canonical root) | `tauri-svelte-preview/src-tauri/src/resources.rs:1691` | 1 of 2 |

The session context panel makes no resource or usage calls of its own; its only backend call is `read_agent_conversation_attachments` (see Chat).

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| none found for this feature | n/a | n/a | n/a | n/a |

### Defined but not called by the UI
- none found in this feature's Rust code (`read_workflow_usage` is called from workflows; see Unassigned).

## Notion
The Tasks panel: Notion sign-in (OAuth) or pasted token, a locally cached task list, and a task detail viewer/editor that reads and writes the Notion page. All calls use `@tauri-apps/api/core` `invoke` directly (desktop only; no remote routing).

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `begin_notion_oauth` | `app/src/lib/shell/notionOAuth.ts:64` | none | `{ authorizationUrl: string, state: string, verifier: string }` | `tauri-svelte-preview/src-tauri/src/notion_oauth.rs:51` | 0 of 1 |
| `claim_notion_oauth` | `app/src/lib/shell/notionOAuth.ts:69` | `{ state: string, verifier: string, claimCode: string }` | `NotionTaskSettings = { dataSourceId: string, hasToken: boolean }` | `tauri-svelte-preview/src-tauri/src/notion_oauth.rs:76` | 1 of 2 |
| `read_notion_task_settings` | `app/src/lib/shell/notionTasks.ts:41` | none | `NotionTaskSettings` | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:217` | 0 of 1 |
| `save_notion_task_settings` | `app/src/lib/shell/notionTasks.ts:48` | `{ dataSourceId: string, token: string }` | `NotionTaskSettings` | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:232` | 0 of 3 |
| `clear_notion_task_settings` | `app/src/lib/shell/notionTasks.ts:52` | none | void | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:348` | 0 of 1 |
| `list_notion_tasks` | `app/src/lib/shell/notionTasks.ts:65` | `{ offset: number, limit: number (default 100), search: string, project: string, statuses: string[], priorities: string[], sortBy: string (default 'taskNumber'), sortDirection: string (default 'desc') }` | `{ tasks: NotionTaskRow[], projects: string[], statuses: string[], priorities: string[], hasMore: boolean }`; row = `{ sourceTaskId, title, project, status, priority: string\|null, assignee: string\|null, dueDate: string\|null, sourceUrl, fetchedAtMs: number }` | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:361` (Rust clamps limit to 100) | 1 of 4 |
| `read_cached_notion_task` | `app/src/lib/shell/notionTasks.ts:79` | `{ sourceTaskId: string }` | `NotionTaskRow \| null` | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:394` | 0 of 1 |
| `refresh_notion_tasks` | `app/src/lib/shell/notionTasks.ts:83` | none | `{ count: number, fetchedAtMs: number }` | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:613` | 1 of 4 |
| `read_notion_task_detail` | `app/src/lib/shell/notionTaskDetail.ts:54` | `{ sourceTaskId: string, requestId: number }` | `{ properties: {name, kind, values: string[], links: (string\|null)[]}[], body: NotionBlock[], comments: {author, created, text}[] }`; block = `{ id, kind, markdown, checked: boolean\|null, icon: string\|null, children: NotionBlock[] }` | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:410` | 0 of 3 |
| `cancel_notion_task_detail` | `app/src/lib/shell/notionTaskDetail.ts:36` | `{ requestId: number }` (sent when the viewer's abort signal fires) | void (errors ignored) | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:439` | 0 of 2 |
| `update_notion_task_status` | `app/src/lib/shell/notionTaskDetail.ts:70` | `{ sourceTaskId: string, status: string }` | UI ignores the result (Rust returns the refresh receipt `{ count, fetchedAtMs }`) | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:450` | 0 of 1 |
| `update_notion_task_property` | `app/src/lib/shell/notionTaskDetail.ts:90` (via `invokeTaskEdit`, :79) | `{ sourceTaskId: string, property: 'title'\|'Priority', value: string }` | UI ignores the result (Rust returns the refresh receipt) | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:485` | 0 of 1 |
| `update_notion_block` | `app/src/lib/shell/notionTaskDetail.ts:99` (via `invokeTaskEdit`, :79) | `{ blockId: string, kind: string, markdown: string }` or `{ blockId, kind, checked: boolean }` | void | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:505` | 0 of 1 |
| `append_notion_task_text` | `app/src/lib/shell/notionTaskDetail.ts:104` (via `invokeTaskEdit`, :79) | `{ sourceTaskId: string, markdown: string }` | void | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:524` | 0 of 1 |
| `append_notion_task_image` | `app/src/lib/shell/notionTaskDetail.ts:115` (via `invokeTaskEdit`, :79) | `{ sourceTaskId: string, fileName: string (default 'screenshot.png'), contentType: string, dataBase64: string }` | void | `tauri-svelte-preview/src-tauri/src/notion_tasks.rs:533` (Rust accepts only image/png, jpeg, gif, webp) | 0 of 1 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| `assembly://notion-oauth?state=<state>&code=<claimCode>` | deep link (plugin-deep-link `onOpenUrl`/`getCurrent`, not a Tauri event) | `state` and `code`; the UI hands them to the waiting sign-in, which sends `code` as `claimCode` to `claim_notion_oauth` | `app/src/routes/+page.svelte:433` (parsed at `app/src/lib/shell/workbenchNavigation.ts:272`, delivered at :287) | not emitted by old Rust: opened by the external Notion callback page; the Rust command comment says so at `tauri-svelte-preview/src-tauri/src/notion_oauth.rs:76` |

### Defined but not called by the UI
- none (every `notion_oauth.rs` / `notion_tasks.rs` command above is called)

## Stacks and processes
The Run (stacks) panel's list of listening processes with their folders, the Playwright browser-session cleanup card, the process stop button and the "what can this build do" capability check.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `list_runtime_contexts` | `app/src/lib/shell/stacks/stackService.ts:166` (via `listRuntimeContextsFromTauri`, `app/src/lib/tauriSource.ts:2849`) | `{ projects: { id, name, path }[] }` | `RuntimeContext[]` = `{ pid: number, command: string, port: number, cwd: string, projectID: string\|null, projectName: string, rootLabel: string }[]` | `tauri-svelte-preview/src-tauri/src/commands/runtime.rs:8` | 0 of 3 |
| `list_playwright_sessions` | `app/src/lib/shell/processes/playwrightBackend.ts:39` (service call at `playwrightService.ts:73`) | none | `PlaywrightSessionInfo[]` = `{ pgid: number, label: string, pids: number[], processes: { pid, pgid, command, name, label, elapsed: string, args: string }[] }[]` | `tauri-svelte-preview/src-tauri/src/commands/runtime.rs:17` | 1 of 2 |
| `kill_playwright_sessions` | `app/src/lib/shell/processes/playwrightBackend.ts:46` | none | `PlaywrightCleanupResult` = `{ sessions: PlaywrightSessionInfo[], terminatedPgids: number[], terminatedPids: number[], failedPgids: { pgid: number, pid: number\|null, message: string }[] }` | `tauri-svelte-preview/src-tauri/src/commands/runtime.rs:31` | 1 of 2 |
| `kill_playwright_session` | `app/src/lib/shell/processes/playwrightBackend.ts:59` (a probe with `pgid: -1` is also sent from `playwrightService.ts:159`) | `{ pgid: number }` | `PlaywrightCleanupResult`; an old build answers with a "command kill_playwright_session" missing error, which the UI reads as "unsupported" (`playwrightBackend.ts:86`) | `tauri-svelte-preview/src-tauri/src/commands/runtime.rs:24` | 0 of 4 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| none | | | | |

### Defined but not called by the UI
- `kill_process` — `tauri-svelte-preview/src-tauri/src/commands/runtime.rs:39` — UI wrapper `killProcess` invokes it at `app/src/lib/shell/processes/processBackend.ts:52` with `{ pid: number, expectedCommand: string|null }` and reads `{ ok: boolean, message: string } | null`, but nothing in the old UI calls `killProcess`.

## Messages
Agent-to-agent message traffic view: pages of messages across this Mac and connected machines, an unread count, and a "something changed" signal.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `list_agent_messages` | `app/src/lib/tauriSource.ts:2277` (via `invokeCancellable`, `:2337`); used `app/src/lib/shell/messages/AgentMessages.svelte:181`, `app/src/lib/shell/workflows/WorkflowsScreen.svelte:180`, `.../WorkflowRuns.svelte:371` | `{ machine, runId, session, status, search: string, sort: 'time'\|'sender'\|'receiver', beforeMs, beforeId, beforeKey, limit, requestId }` (nulls when unset) | `AgentMessage[] { id, createdAtMs, senderName, fromOwnedId, toOwnedId, receiver, body, receipt: 'queued'\|'delivered'\|'failed'\|'expired', runId, kind, machine }` or null if aborted | `RS/agent_conversation/mod.rs:884` (fan-out via `remote.list_messages`; remote server arm `RemoteCommand::Messaging` at `remote/server/mod.rs:306`, handler `RS/agent_conversation/messaging.rs`) | 2 of 4 |
| `count_agent_messages` | `app/src/lib/tauriSource.ts:2292` (via `invokeCancellable`); used `app/src/routes/+page.svelte:112` | `{ session, afterMs, requestId }` | `number` or null if aborted | `RS/agent_conversation/mod.rs:903` | 0 of 2 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| `agent-messages-changed` | event | none (payload ignored; UI rereads) | `app/src/lib/tauriSource.ts:2300`; users: `AgentMessages.svelte:148`, `routes/+page.svelte:127`, `board/BoardButton.svelte:55`, `panels/agents/AgentsPanel.svelte:98` | `RS/main.rs:3465` (local broker), `RS/main.rs:3467` (remote connection) |

### Defined but not called by the UI
- none beyond the two above in this feature (`list_owner_inbox` is in Unassigned).

## Build Board
The Workflows screen and the owner's board: create, start, pause, resume, cancel, retry, answer and approve workflow runs (on this Mac or a remote machine), read run history, usage and review reports, and the board of cards, runs, waiting steps and questions. Workflow functions are in `app/src/lib/shell/workflows/workflowService.ts`; each local call goes to a `…FromTauri` wrapper in `app/src/lib/tauriSource.ts`. When a remote machine (`machine` = remote profile id) is chosen, the same operation is sent as `remote_workflow` instead (see that row). Every run-returning command answers with the full run: `WorkflowRunRecord` (`app/src/lib/shell/workflows/workflowTypes.ts:233`). `idempotencyKey: string` is sent on every mutation.

| Command | Called from | Args sent | Answer expected | Old handler | Fix commits |
|---|---|---|---|---|---|
| `list_workflow_runs` | `app/src/lib/tauriSource.ts:2895`, `app/src/lib/shell/workflows/workflowService.ts:50` | none | `WorkflowRunRecord[]` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:6` | 1 of 7 |
| `workflow_history` | `app/src/lib/tauriSource.ts:2901`, `app/src/lib/shell/workflows/workflowService.ts:61` | `{ runId: string }` | `WorkflowHistoryRow[]` = `{ sequence, timestampMs, kind, nodeId\|null, attempt\|null, ownedId\|null, status\|null, commands: {command, exitCode\|null}[], findingCount\|null, verdict\|null, files: string[], … }` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:97` | 0 of 3 |
| `create_workflow_run` | `app/src/lib/tauriSource.ts:2913`, `app/src/lib/shell/workflows/workflowService.ts:70` | `{ repoRoot: string, workflowPath: string (relative to repoRoot), task: { key, title, body }, input: Record<string, unknown>, idempotencyKey }` | `WorkflowRunRecord` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:71` | 2 of 7 |
| `list_workflow_files` | `app/src/lib/tauriSource.ts:2944`, `app/src/lib/shell/workflows/workflowService.ts:74` | `{ repoRoot: string }` | `string[]` (paths relative to repoRoot) | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:139` | 1 of 5 |
| `start_workflow_run` | `app/src/lib/tauriSource.ts:2962`, `app/src/lib/shell/workflows/workflowService.ts:90` | `{ runId, idempotencyKey }` | `WorkflowRunRecord` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:162` | 0 of 4 |
| `pause_workflow_run` | `app/src/lib/tauriSource.ts:2968`, `app/src/lib/shell/workflows/workflowService.ts:98` | `{ runId, idempotencyKey }` | `WorkflowRunRecord` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:177` | 1 of 5 |
| `resume_workflow_run` | `app/src/lib/tauriSource.ts:2974`, `app/src/lib/shell/workflows/workflowService.ts:106` | `{ runId, idempotencyKey }` | `WorkflowRunRecord` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:191` | 0 of 4 |
| `cancel_workflow_run` | `app/src/lib/tauriSource.ts:2980`, `app/src/lib/shell/workflows/workflowService.ts:114` | `{ runId, idempotencyKey }` | `WorkflowRunRecord` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:206` | 0 of 3 |
| `retry_workflow_node` | `app/src/lib/tauriSource.ts:2986`, `app/src/lib/shell/workflows/workflowService.ts:122` | `{ runId, nodeId, idempotencyKey }` | `WorkflowRunRecord` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:221` | 1 of 4 |
| `answer_workflow_pause` | `app/src/lib/tauriSource.ts:2956`, `app/src/lib/shell/workflows/workflowService.ts:82` | `{ runId, nodeId, decision: 'another-round'\|'accept'\|'stop', reply: string\|null, idempotencyKey }` | `WorkflowRunRecord` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:144` | 2 of 9 |
| `approve_workflow_gate` | `app/src/lib/tauriSource.ts:2997`, `app/src/lib/shell/workflows/workflowService.ts:130` | `{ runId, nodeId, approval: { approved: boolean, … }, idempotencyKey }` | `WorkflowRunRecord` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:237` | 2 of 9 |
| `watch_workflow_runs` | `app/src/lib/shell/workflows/workflowService.ts:209` (screen calls at `WorkflowsScreen.svelte:100,107`) | `{ board: boolean, runId: string\|null }` | void (tells the engine which runs to push on `workflow-run-changed`) | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:22` | 0 of 1 |
| `read_workflow_usage` | `app/src/lib/shell/workflows/workflowService.ts:227` | `{ runId: string\|null }` | `WorkflowUsageRow[]` = `{ nodeRunId, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, reportedCostMicros: number\|null, estimatedCostMicros: number\|null }[]` | `tauri-svelte-preview/src-tauri/src/usage_history.rs:69` | 0 of 3 |
| `workflow_review_reports` | `app/src/lib/shell/workflows/workflowService.ts:235` | `{ runId: string }` | `{ attemptId: string, report: unknown, atMs: number }[]` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:106` | 0 of 1 |
| `list_owner_inbox` | `app/src/lib/tauriSource.ts:2323` (via `invokeCancellable`, :2327; used by `OwnerInbox.svelte:78`) | `{ requestId: number }` | `OwnerInboxStep[]` = `{ runId, taskKey\|null, workflowName\|null, nodeId, state: 'waiting-input'\|'waiting-approval', reason\|null, ownedId\|null, updatedAtMs, machine: string }[]` (`machine` = `'mac'` or remote profile id) | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:116` | 2 of 4 |
| `remote_workflow` | `app/src/lib/tauriSource.ts:2931` (via `remoteWorkflowFromTauri`; called from `workflowService.ts:50,60,69,74,81,89,97,105,113,121,129,224,232`) | `{ profileId: string, operation: string, args: object, requestId: number\|null }`; operations the UI sends: `list` `{}`, `history` `{runId}`, `create` (create DTO), `listFiles` `{repoRoot}`, `answer`, `start`, `pause`, `resume`, `cancel`, `retry`, `approveGate` (same DTOs as the local rows), `usage` `{runId}`, `reviews` `{runId}` | same shapes as the matching local command; null if the abort signal fired | `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/mod.rs:5441`, executed on the remote server by the dispatcher at `tauri-svelte-preview/src-tauri/src/agent_conversation/remote/server/mod.rs:269` (`usage` handled inline there; others by `execute_operation` at `tauri-svelte-preview/src-tauri/src/workflow/mod.rs:3542`) | 1 of 4 |
| `board` | `app/src/lib/shell/board/boardSource.ts:46`, `app/src/lib/shell/board/boardSource.ts:76` (via `invokeCancellable`) | `{ op: string, args: object, requestId: number }`; ops: `board` `{today_ms, now_ms}`, `board_count` `{now_ms}`, `board_card` `{machine, key, action: 'done'\|'snooze'\|'drop'\|'undo'\|'clear_done', at}`, `board_note` `{machine, target, body, now_ms}`, `board_question` `{machine, id, asked_at_ms, answer, now_ms}` | `board` -> `MachineBoard[]` = `{ machine, value: { cards, runs, steps, counts, session_notes, questions? } }[]`; `board_count` -> `{ count, snoozed_until_ms }`; card/note/question ops -> `boolean`; null when cancelled | `tauri-svelte-preview/src-tauri/src/agent_conversation/messaging/board.rs:159` (op routing in `board_op`, :136; the store query is `core/src/session_store/board.rs:276`) | 5 of 18 |

### Events and streams
| Name | Kind (event / channel) | Payload the UI reads | Listened in | Emitted by (old Rust) |
|---|---|---|---|---|
| `workflow-run-changed` | event | `WorkflowRunRecord` (the open run in full; other runs without node `structuredOutput`/`artifacts`) | `app/src/lib/shell/workflows/workflowService.ts:215` | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:37` (`send_to_watchers`); driven by `forward_run_changes` at :44 (250 ms batching) |
| `workflow-run-updated` | event | `WorkflowRunRecord \| WorkflowRunRecord[]` | `app/src/lib/shell/workflows/workflowService.ts:173` (via `subscribeWorkflowSnapshots`, used by `WorkflowRuns.svelte:132`; event name constant at :44) | `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:12` (`emit_workflow_run_updated`, after each mutation command) |

### Defined but not called by the UI
- `get_workflow_run` — `tauri-svelte-preview/src-tauri/src/workflow/commands.rs:88`
- `list_orchestration_runs` — `tauri-svelte-preview/src-tauri/src/workflow/orchestration_commands.rs:9` — UI wrapper `listOrchestrationRunsFromTauri` invokes it at `app/src/lib/tauriSource.ts:2878` with `{ projects: {id,name,path}[] }` expecting `OrchestrationRun[]` (`tauriSource.ts:690`), but nothing calls the wrapper.
- `record_orchestration_event` — `tauri-svelte-preview/src-tauri/src/workflow/orchestration_commands.rs:20` — wrapper `recordOrchestrationEventToTauri` invokes it at `app/src/lib/tauriSource.ts:2889` with `{ event: OrchestrationEvent }` expecting `OrchestrationRun`, but nothing calls the wrapper.
- `copy_evidence_original` — `tauri-svelte-preview/src-tauri/src/evidence.rs:143` (evidence gallery)
- `list_evidence_gallery` — `tauri-svelte-preview/src-tauri/src/evidence.rs:160` (evidence gallery)
- `set_evidence_pinned` — `tauri-svelte-preview/src-tauri/src/evidence.rs:216` (evidence gallery)
- `resolve_evidence_original` — `tauri-svelte-preview/src-tauri/src/evidence.rs:245` (evidence gallery)
- `delete_evidence` — `tauri-svelte-preview/src-tauri/src/evidence.rs:275` (evidence gallery)
- `read_evidence_disk_usage` — `tauri-svelte-preview/src-tauri/src/evidence.rs:294` (evidence gallery)
