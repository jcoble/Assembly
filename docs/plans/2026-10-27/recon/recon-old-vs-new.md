Recon, read-only, 2026-10-27, not re-verified unless marked

# Old app vs new .NET backend socket: recon

Read-only. Old = mac-command-bar/tauri-svelte-preview/src-tauri/src (HEAD d5f5fe68c). New = worktrees/Assembly/base-cut/dotnet (HEAD 60ce21e, which is the base-cut checkout; caller named e8e361e, not checked out here - assumed same socket surface). Marks: verified = I read the line; assumed = inferred.

## 1. Old side

### 1a. Tauri commands
- 303 `#[tauri::command]` fns (grep, verified); 290 are registered in `generate_handler!` at main.rs:3565-3855 (verified). Each command's definition file:line appears in the table in section 3.
- Front end direct `invoke('name')` calls: 228 distinct, list with first file:line in scratchpad `fe-invokes.txt` (verified by grep; wrappers in src/lib/tauriSource.ts and workspaceInvoke.ts hide more, so the registered list is the safe superset).
- FE invokes with no registered handler (verified by diff): list_agui_events_after, list_agui_events_before, run_agui, read_proof_mode_label, read_usage_breakdown, workflow_history, workflow_review_reports, write_assembly_setting, and the remote workspace ops workspace_exists/mkdir/read_dir/rename/copy_file/write_text (these go over the remote socket's `Workspace{operation}` op, remote/server/socket.rs:93-99).

### 1b. Tauri events and channels (FE listens)
- `provider-download-progress` providerUpdateService.svelte.ts:79
- `workflow-run-changed` workflowService.ts:215
- `browser-tab-navigation` browserBackend.ts:111
- `assembly_exit_requested` tauriSource.ts:918
- `agent-conversation-history-download` tauriSource.ts:2155
- `agent-messages-changed` tauriSource.ts:2300
- Tauri `Channel` streams: tauriSource.ts:1271 (conversation/terminal projection envelopes), :2414, :2455, :2481 (status/log lines for installs)
- Backend also emits `workflow-broker-event`, `workflow-run-updated` (grep emit; FE does not listen directly).

### 1c. Old remote host routes and socket
- WebSocket `/assembly` remote server: remote/server/mod.rs:71 (verified). Client frames remote/wire.rs:25-31: Authenticate, Request{id,command}, Cancel, Resume{cursors}, RelayReply. Commands (wire.rs:36-139): Workspace, ProbeProviderConfig, ListSessions, CheckProviderUpdates, InstallProviderUpdates, RestartForProviderUpdates, Ensure, Snapshot, ExtendImport, ScanTranscripts, BeginImport, SelectChildHistory, StopChildHistory, DownloadHistory, EventsAfter, Capabilities, Config, WarmConfig, SetConfig, SetConfigOption, Set/Get/ClearDraft, Write/Read/Delete workspace + expanded paths, ChangeCheckout, UpdateMeta, Close, Delete, AttachmentChunk, AbortAttachmentUpload, ReadAttachments, ReadSelectedAttachments, ReadAttachmentChunk, DeleteAttachment, Send, RespondApproval, RespondPermission, RespondInput, Stop, Workflow{operation}, Messaging{op}, TerminalStart/Write/Resize/Scrollback/Close. Server frames (wire.rs:177-192): Ready, Response, Error, Event, ReplayStart, ReplayComplete, MessagesChanged, HistoryDownloadStart/Chunk/Done, ProviderDownloadProgress, TerminalOutput, RelayRequest. Workspace sub-ops (socket.rs:27-106): git, worktree, GitHub PR, file read/write, search, mkdir/rename/trash etc.
- WebSocket `/lsp` native C# language server: files/lsp.rs:1921 (FE csharpLanguageClient.ts:103).
- HTTP `/mcp` messaging MCP: agent_conversation/messaging/mcp.rs:18 (bearer token).
- HTTP phone API `/phone/v1/*` (28 routes): agent_conversation/phone/mod.rs:91-121 (pair, me, sessions, chat, attachments, items, changes, files, file, config, git/*, terminal, runs, inbox, live, push-token).

## 2. New side: what the socket offers

Transport: `/socket` WebSocket (ApiServer.cs:59). Frames in: call, sub, unsub, cancel (SocketConnection.cs:163-190); out: welcome, result, error (SocketFrames.cs:13,19,30). Other: `/live` (ApiServer.cs:46); HTTP routes in Assembly.Infrastructure/Http: /sessions, /sessions/{id}, /sessions/{id}/rows, /sessions/{id}/tools/{item}, /sessions/{id}/send, /runs/{id}/stop, /sessions/{id}/asks/{ask}, /sessions/{id}/tasks/{task}/stop, /outbox*, /version, /machines, /agents, /debug/drop, `/{feature}/{method}` (FeatureCalls.cs:33), `/mcp` (McpRoutes).

| Kind | Name | file:line |
|---|---|---|
| Read | `diagnostics/memory.sample` | Assembly.Application/Features/Diagnostics/DiagnosticsFeature.cs:24 |
| Read | `diagnostics/activity.begin` | DiagnosticsFeature.cs:25 |
| Read | `diagnostics/activity.end` | DiagnosticsFeature.cs:26 |
| Read | `diagnostics/drop` | DiagnosticsFeature.cs:27 |
| Read | `diagnostics/soak.append` | DiagnosticsFeature.cs:28 |
| Rows (topic) | `rows` | Assembly.Application/Features/Chat/Rows/RowsFeature.cs:48 |
| Read | `rows/page` | RowsFeature.cs:63 |
| Read | `rows/tool` | RowsFeature.cs:71 |
| Topic | `preferences` | Assembly.Application/Features/Preferences/PreferencesFeature.cs:26 |
| Save | `preferences (set)` | PreferencesFeature.cs:28 |
| Read | `config/get` | Assembly.Application/Features/Chat/Sessions/ConfigFeature.cs:30 |
| Write | `config/set` | ConfigFeature.cs:35 |
| Read | `machines/list` | Assembly.Application/Features/Chat/Sessions/MachinesFeature.cs:20 |
| Read | `machines/agents` | MachinesFeature.cs:22 |
| Topic | `sessions` | Assembly.Application/Features/Chat/Sessions/SessionsFeature.cs:31 |
| Read | `sessions/page` | SessionsFeature.cs:35 |
| Write | `sessions/create` | SessionsFeature.cs:43 |
| Topic | `clock` | Assembly.Infrastructure/Clock/BackendClock.cs:32 |
| Write | `send/send` | Assembly.Application/Features/Chat/Sessions/SendFeature.cs:36 |
| Write | `send/stop` | SendFeature.cs:46 |
| Write | `send/answer` | SendFeature.cs:52 |
| Write | `send/stopTask` | SendFeature.cs:61 |
| Write | `attachments/begin` | Assembly.Infrastructure/Chat/Attachments/AttachmentsFeature.cs:55 |
| Write | `attachments/chunk` | AttachmentsFeature.cs:85 |
| Write | `attachments/commit` | AttachmentsFeature.cs:108 |
| Write | `attachments/drop` | AttachmentsFeature.cs:127 |
| Read | `attachments/read` | AttachmentsFeature.cs:132 |

Total 27 registrations (grep, verified). MCP helper tools (not socket): start_session, list_sessions, send_message, message_status (Application/Features/Chat/Helpers/*Tool.cs).

## 3. The map
OLD = /home/blackcolours/dev/work/mac-command-bar/tauri-svelte-preview/src-tauri/src/ is the base for old file:line.

### Sessions (list, create, lifecycle, meta)
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `list_agent_conversation_sessions` | agent_conversation/mod.rs:915 | topic `sessions` (SessionsFeature.cs:31) + `sessions/page` (:35) | covered for local list; verified |
| `list_agent_sessions` | commands/agent_sessions.rs:17 | topic `sessions` | old = scanned transcripts of provider CLIs; new lists only own store; assumed |
| `list_agent_sessions_for_project` | commands/agent_sessions.rs:34 | **missing** | no project filter arg on `sessions/page` (PageArgs has before/limit only; SessionsFeature.cs:35-38) |
| `read_agent_session_details` | commands/agent_sessions.rs:46 | **missing** | old reads native transcript details; no equivalent |
| `read_remote_saved_session_details` | commands/agent_sessions.rs:65 | **missing** |  |
| `list_remote_agent_conversation_sessions` | agent_conversation/mod.rs:941 | topic `sessions` (local only) | remote sessions not merged into the topic; assumed - see machines |
| `ensure_agent_conversation` | agent_conversation/mod.rs:109 | `sessions/create` (SessionsFeature.cs:43) | create needs agent+repoUrl; old ensure also resumed/attached; partial |
| `close_agent_conversation` | agent_conversation/mod.rs:772 | **missing** | no close |
| `delete_agent_conversation_session` | agent_conversation/mod.rs:787 | **missing** | no delete |
| `update_agent_conversation_session_meta` | agent_conversation/mod.rs:1047 | **missing** | rename/pin/archive etc. |
| `read_agent_conversation_selection` | agent_conversation/mod.rs:823 | **missing** |  |
| `handoff_agent_conversation` | agent_conversation/handoff.rs:140 | **missing** |  |
| `set_session_project` | agent_conversation/session_project.rs:27 | **missing** | new has no projects concept |
| `begin_agent_conversation_import` | agent_conversation/mod.rs:1118 | **missing** | import from native CLI transcripts |
| `extend_agent_conversation_import` | agent_conversation/mod.rs:1149 | **missing** |  |
| `finish_agent_conversation_import` | agent_conversation/mod.rs:1136 | **missing** |  |
| `agent_conversation_add_session_annotation` | agent_conversation/mod.rs:71 | **missing** |  |
| `agent_conversation_list_session_annotations` | agent_conversation/mod.rs:87 | **missing** |  |
| `agent_conversation_delete_session_annotation` | agent_conversation/mod.rs:100 | **missing** |  |
| `change_agent_conversation_checkout` | agent_conversation/mod.rs:606 | **missing** | checkout switch of a session |
| `read_agent_conversation_child_history` | agent_conversation/mod.rs:1060 | **missing** | subagent history; new has Subagents.cs but no call |
| `stop_agent_conversation_child_history` | agent_conversation/mod.rs:1091 | **missing** |  |
| `read_agent_conversation_history_download` | agent_conversation/mod.rs:815 | **missing** | bulk gzip history download |
| `read_agent_conversation_capabilities` | agent_conversation/mod.rs:758 | **missing** |  |
| `read_agent_conversation_tool_call` | agent_conversation/manager/tool_call.rs:105 | `rows/tool` (RowsFeature.cs:71) | verified |

### Chat / rows (transcript, events, live)
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `list_agent_conversation_events` | agent_conversation/mod.rs:956 | topic `rows` (RowsFeature.cs:48) + `rows/page` (:63) | new rows model replaces events; verified |
| `list_agent_conversation_events_before` | agent_conversation/mod.rs:966 | `rows/page` (:63) | paging back; verified |
| `list_agent_conversation_items_before` | agent_conversation/mod.rs:980 | `rows/page` | assumed |
| `register_agent_conversation_stream` | projection_streams.rs:523 | topic `rows` subscription (sub frame, SocketConnection.cs:170) | old Tauri Channel envelopes (tauriSource.ts:1271); replaced by sub frames |
| `acknowledge_agent_conversation_stream` | projection_streams.rs:532 | n/a (sub/backpressure in socket) | LiveFellBehind.cs exists; assumed |
| `unregister_agent_conversation_stream` | projection_streams.rs:542 | `unsub` frame (SocketConnection.cs:177) | verified |
| `close_agui_view` | agent_conversation/agui.rs:320 | **missing** | AGUI projection not in new |
| `run_agui / list_agui_events_after / list_agui_events_before` | - | **missing** | FE-only names, not registered in handler list; AGUI |
| `start_agent_conversation_terminal_projection` | agent_conversation/mod.rs:1171 | **missing** |  |
| `stop_agent_conversation_terminal_projection` | agent_conversation/mod.rs:1181 | **missing** |  |

### Send / turn control / approvals
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `send_agent_conversation_message` | agent_conversation/mod.rs:143 | `send/send` (SendFeature.cs:36) | verified |
| `stop_agent_conversation_turn` | agent_conversation/mod.rs:589 | `send/stop` (SendFeature.cs:46) | verified |
| `cancel_agent_conversation_request` | agent_conversation/mod.rs:859 | `cancel` frame (SocketConnection.cs:179) | that cancels a socket call, not an agent request; assumed partial |
| `respond_agent_conversation_input` | agent_conversation/mod.rs:561 | `send/answer` (SendFeature.cs:52) | verified |
| `respond_agent_conversation_approval` | agent_conversation/mod.rs:515 | `send/answer` | assumed (one answer call for asks) |
| `respond_agent_conversation_permission` | agent_conversation/mod.rs:538 | `send/answer` | assumed |
| `agent_conversation_set_session_draft` | agent_conversation/mod.rs:173 | **missing** | drafts |
| `agent_conversation_get_session_draft` | agent_conversation/mod.rs:187 | **missing** |  |
| `agent_conversation_clear_session_draft` | agent_conversation/mod.rs:200 | **missing** |  |
| `(none in old as a command) stop background task` | - | `send/stopTask` (SendFeature.cs:61) | new only |

### Config (models, modes, provider config)
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `read_agent_conversation_config` | agent_conversation/mod.rs:744 | `config/get` (ConfigFeature.cs:30) | verified |
| `set_agent_conversation_config` | agent_conversation/mod.rs:624 | `config/set` (ConfigFeature.cs:35) | verified |
| `set_agent_conversation_config_option` | agent_conversation/mod.rs:652 | `config/set` (configId+value) | verified |
| `warm_agent_conversation_config` | agent_conversation/mod.rs:684 | **missing** |  |
| `probe_agent_provider_config` | agent_conversation/mod.rs:702 | **missing** |  |
| `cancel_agent_provider_probe` | agent_conversation/mod.rs:732 | **missing** |  |
| `list_provider_descriptors` | agent_conversation/providers/descriptors.rs:232 | `machines/agents` (MachinesFeature.cs:22) | agents list per machine; partial |
| `check_provider_clis` | agent_conversation/providers/cli_updates.rs:144 | **missing** |  |
| `update_provider_cli` | agent_conversation/providers/cli_updates.rs:158 | **missing** |  |
| `install_provider_updates` | agent_conversation/providers/updates.rs:100 | **missing** |  |
| `restart_for_provider_updates` | agent_conversation/providers/updates.rs:147 | **missing** |  |
| `check_remote_provider_updates` | agent_conversation/remote/mod.rs:5452 | **missing** |  |
| `install_remote_provider_updates` | agent_conversation/remote/mod.rs:5463 | **missing** |  |
| `restart_remote_for_provider_updates` | agent_conversation/remote/mod.rs:5490 | **missing** |  |

### Machines / remote
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `read_remote_assembly_environment` | agent_conversation/remote/mod.rs:2516 | `machines/list` (MachinesFeature.cs:20) | list only |
| `read_remote_backend_statuses` | agent_conversation/remote/mod.rs:2621 | `machines/list` | assumed |
| `connect_remote_assembly` | agent_conversation/remote/mod.rs:2523 | **missing** | no add/connect call; new RemoteMachine.cs uses HTTP to a remote (RemoteMachine.cs:63-98) but no socket call to add one |
| `install_remote_assembly` | agent_conversation/remote/install_flow.rs:18 | **missing** |  |
| `uninstall_remote_assembly` | agent_conversation/remote/mod.rs:2649 | **missing** |  |
| `cancel_remote_connection` | agent_conversation/remote/mod.rs:2665 | **missing** |  |
| `disconnect_remote_assembly` | agent_conversation/remote/mod.rs:2670 | **missing** |  |
| `remove_remote_assembly_profile` | agent_conversation/remote/mod.rs:2678 | **missing** |  |
| `deploy_remote_backend_dev` | agent_conversation/remote/mod.rs:2557 | **missing** | debug-only |
| `remote_workspace` | agent_conversation/remote/mod.rs:5430 | **missing** | proxied workspace ops to remote |
| `remote_workflow` | agent_conversation/remote/mod.rs:5441 | **missing** |  |

### Terminals
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `start_terminal_session` | terminal/commands.rs:13 | **missing** |  |
| `list_terminal_sessions` | terminal/commands.rs:33 | **missing** |  |
| `read_terminal_session_scrollback` | terminal/commands.rs:40 | **missing** |  |
| `write_terminal_session` | terminal/commands.rs:53 | **missing** |  |
| `resize_terminal_session` | terminal/commands.rs:66 | **missing** |  |
| `close_terminal_session` | terminal/commands.rs:80 | **missing** |  |
| `open_terminal_path` | files/commands.rs:238 | **missing** |  |
| `open_terminal_command` | files/commands.rs:250 | **missing** |  |
| `acknowledge_terminal_output_stream` | projection_streams.rs:559 | **missing** |  |
| `unregister_terminal_output_stream` | projection_streams.rs:569 | **missing** |  |

### Git / worktrees / pull requests
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `project_git_status` | git/commands.rs:23 | **missing** |  |
| `read_source_git_diff` | git/commands.rs:30 | **missing** |  |
| `read_git_branch_diff` | git/commands.rs:42 | **missing** |  |
| `stage_git_paths` | git/commands.rs:49 | **missing** |  |
| `unstage_git_paths` | git/commands.rs:59 | **missing** |  |
| `commit_git_repository` | git/commands.rs:69 | **missing** |  |
| `fetch_git_repository` | git/commands.rs:81 | **missing** |  |
| `pull_git_repository` | git/commands.rs:88 | **missing** |  |
| `push_git_repository` | git/commands.rs:95 | **missing** |  |
| `publish_git_repository` | git/commands.rs:102 | **missing** |  |
| `add_git_remote` | git/commands.rs:109 | **missing** |  |
| `read_git_commit_history` | git/commands.rs:116 | **missing** |  |
| `read_git_commit_files` | git/commands.rs:129 | **missing** |  |
| `read_git_commit_file_diff` | git/commands.rs:141 | **missing** |  |
| `discard_git_paths` | git/workspace.rs:519 | **missing** |  |
| `discard_all_git_changes` | git/workspace.rs:529 | **missing** |  |
| `list_git_branches` | git/workspace.rs:541 | **missing** |  |
| `create_git_branch` | git/workspace.rs:548 | **missing** |  |
| `switch_git_branch` | git/workspace.rs:561 | **missing** |  |
| `stash_git_changes` | git/workspace.rs:568 | **missing** |  |
| `pop_git_stash` | git/workspace.rs:585 | **missing** |  |
| `list_git_stashes` | git/workspace.rs:592 | **missing** |  |
| `amend_git_commit` | git/workspace.rs:599 | **missing** |  |
| `list_project_worktrees` | git/commands.rs:154 | **missing** |  |
| `list_repository_checkouts` | git/commands.rs:161 | **missing** |  |
| `remove_project_worktree` | git/commands.rs:178 | **missing** |  |
| `archive_project_worktree` | git/commands.rs:192 | **missing** |  |
| `list_git_repository_summaries` | git/commands.rs:204 | **missing** |  |
| `workspace_git_init` | project_folders.rs:294 | **missing** |  |
| `project_root_is_bare` | project_folders.rs:285 | **missing** |  |
| `generate_commit_message` | git/pull_requests.rs:63 | **missing** |  |
| `read_pull_request_context` | git/pull_requests.rs:98 | **missing** |  |
| `generate_pull_request_details` | git/pull_requests.rs:107 | **missing** |  |
| `create_pull_request` | git/pull_requests.rs:137 | **missing** |  |
| `read_pull_request_status` | git/pull_requests.rs:152 | **missing** |  |
| `list_github_pull_requests` | git/github.rs:202 | **missing** |  |
| `read_github_pull_request` | git/github.rs:211 | **missing** |  |
| `read_github_pull_request_file` | git/github.rs:218 | **missing** |  |
| `submit_github_pull_request_review` | git/github.rs:225 | **missing** |  |
| `reply_github_pull_request_comment` | git/github.rs:232 | **missing** |  |
| `comment_github_pull_request` | git/github.rs:239 | **missing** |  |
| `merge_github_pull_request` | git/github.rs:246 | **missing** |  |
| `list_open_pull_requests` | git/workspace.rs:611 | **missing** |  |
| `(new only) session checkout` | - | `GitCheckout.cs` in Infrastructure/Agents, used by `sessions/create` (repoUrl) | no git status/diff call; assumed from file listing |

### Files / source / LSP
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `list_source_files` | files/commands.rs:35 | **missing** |  |
| `list_source_directory` | files/commands.rs:66 | **missing** |  |
| `cancel_source_scan` | files/commands.rs:98 | **missing** |  |
| `validate_project_root` | files/commands.rs:106 | **missing** |  |
| `cancel_project_root_validation` | files/commands.rs:125 | **missing** |  |
| `read_source_file` | files/commands.rs:133 | **missing** |  |
| `read_source_image` | files/commands.rs:152 | **missing** |  |
| `cancel_source_file_reads` | files/commands.rs:162 | **missing** |  |
| `read_native_csharp_file` | files/commands.rs:170 | **missing** |  |
| `write_source_file` | files/commands.rs:189 | **missing** |  |
| `open_source_file` | files/commands.rs:202 | **missing** |  |
| `reveal_source_file` | files/commands.rs:211 | **missing** |  |
| `open_path` | files/commands.rs:220 | **missing** |  |
| `reveal_path` | files/commands.rs:229 | **missing** |  |
| `move_to_trash` | files/commands.rs:386 | **missing** |  |
| `search_source_files` | files/commands.rs:263 | **missing** |  |
| `search_source_tree` | files/commands.rs:274 | **missing** |  |
| `find_source_definitions` | files/commands.rs:309 | **missing** |  |
| `find_source_definitions_in_root` | files/commands.rs:322 | **missing** |  |
| `find_source_references` | files/commands.rs:337 | **missing** |  |
| `find_source_references_in_root` | files/commands.rs:350 | **missing** |  |
| `count_source_references` | files/commands.rs:365 | **missing** |  |
| `list_folders` | project_folders.rs:273 | **missing** |  |
| `LSP family (31 commands: read_source_lsp_status ... find_source_lsp_*, format_source_with_lsp, rename_source_with_lsp, set_*language_server*, read_workspace_language_intelligence, ensure_native_csharp_language_client ...)` | - | **missing** | files/lsp_commands.rs:4-473 plus websocket `/lsp` (files/lsp.rs:1921; FE csharpLanguageClient.ts:103) |
| `workspace_exists / workspace_mkdir / workspace_rename / workspace_copy_file / workspace_write_text / workspace_read_dir` | - | **missing** | FE invokes (remote workspace ops, remote/workspace.rs:93-99); not in generate_handler |

### Attachments
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `save_agent_conversation_attachment` | agent_conversation/mod.rs:1190 | `attachments/begin`,`chunk`,`commit` (AttachmentsFeature.cs:55,85,108) | verified |
| `read_agent_conversation_attachments` | agent_conversation/mod.rs:1216 | **missing** | listing of a session's attachments |
| `read_agent_conversation_selected_attachments` | agent_conversation/mod.rs:1233 | **missing** |  |
| `read_agent_conversation_attachment_file` | agent_conversation/mod.rs:1259 | `attachments/read` (AttachmentsFeature.cs:132) | verified |
| `discard_agent_conversation_original` | agent_conversation/mod.rs:1330 | **missing** |  |
| `delete_agent_conversation_attachment` | agent_conversation/mod.rs:1358 | `attachments/drop` (AttachmentsFeature.cs:127) | drop may be upload-abort only; assumed |

### Settings / preferences / workspace layout
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `read_assembly_setting` | agent_conversation/mod.rs:506 | topic `preferences` (PreferencesFeature.cs:26) | verified |
| `write_assembly_setting` | agent_conversation/mod.rs:291 | `preferences` Save (PreferencesFeature.cs:28) | FE invoke, not registered in handler list; verified |
| `read_helper_settings` | helper/mod.rs:501 | **missing** | helper (LLM key) settings |
| `write_helper_settings` | helper/mod.rs:520 | **missing** |  |
| `set_helper_key` | helper/mod.rs:486 | **missing** |  |
| `test_helper` | helper/mod.rs:529 | **missing** |  |
| `run_helper_job` | helper/mod.rs:471 | **missing** |  |
| `write_agent_conversation_workspace` | agent_conversation/mod.rs:212 | **missing** | per-session UI layout persistence (could ride preferences; not designed) |
| `read_agent_conversation_workspace` | agent_conversation/mod.rs:225 | **missing** |  |
| `read_agent_conversation_workspace_expanded_paths` | agent_conversation/mod.rs:237 | **missing** |  |
| `write_agent_conversation_workspace_expanded_paths` | agent_conversation/mod.rs:251 | **missing** |  |
| `delete_agent_conversation_workspace` | agent_conversation/mod.rs:265 | **missing** |  |
| `clear_agent_conversation_workspace_editors` | agent_conversation/mod.rs:277 | **missing** |  |
| `clear_agent_conversation_workspace_tabs` | agent_conversation/mod.rs:284 | **missing** |  |
| `list_installed_font_families` | commands/application.rs:16 | **missing** | GPUI-side more likely |
| `list_projects / add_project / rename_project / set_project_root / set_project_pinned / remove_project` | - | **missing** | agent_conversation/mod.rs:336-480 |
| `list_folders` | project_folders.rs:273 | **missing** | project_folders.rs:273 |

### Notifications / messaging between sessions
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `list_agent_messages` | agent_conversation/mod.rs:884 | **missing** | agent_conversation/mod.rs:884 |
| `board (Build Board)` | - | **missing** | messaging/board.rs:159 |
| `set_main_orchestrator` | agent_conversation/mod.rs:873 | **missing** | mod.rs:873 |
| `event `agent-messages-changed` (tauriSource.ts:2300)` | - | **missing** |  |
| `MCP `/mcp` helper tools (messaging/mcp.rs:18)` | - | `/mcp` route (Infrastructure/Mcp/McpRoutes.cs) + Helpers tools start_session, list_sessions, send_message, message_status (Helpers/*.cs) | partial; no board tools; verified names |
| `phone push (phone/push.rs) / `/phone/v1/*`` | - | **missing** | phone/mod.rs:91-121 |

### Other backend features
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `workflow family (list/create/start/pause/resume/cancel/retry/approve, watch, inbox, files; 19 cmds)` | - | **missing** | workflow/commands.rs:6-237, orchestration_commands.rs:9-20; event workflow-run-changed workflowService.ts:215 |
| `notion task family (settings, list, detail, status, property, block, append, refresh, oauth; 20 cmds)` | - | **missing** | notion_tasks.rs:217-613, notion_oauth.rs:51-76 |
| `usage family (read_usage_*, read_current_provider_usage, refresh_usage_history, read_workflow_usage; 11 cmds)` | - | **missing** | usage_history.rs:44-117, usage_current.rs:41, commands/usage.rs |
| `resources family (snapshot, sample, totals, disk scan/usage, stop_owned_resource, apply_resource_memory_pressure ...; 17 cmds)` | - | `diagnostics/memory.sample` (DiagnosticsFeature.cs:24) covers sample only | resources.rs:289-1716 |
| `evidence family (gallery, pin, delete, resolve, copy, disk usage; 6 cmds)` | - | **missing** | evidence.rs:143-294 |
| `runtime: list_runtime_contexts, list_playwright_sessions, kill_playwright_session(s), kill_process` | - | **missing** | commands/runtime.rs:8-39 |
| `read_backend_capabilities` | commands/application.rs:11 | `welcome` frame (SocketConnection.cs:150) gives protocol+machine only | commands/application.rs:11; partial |
| `(new only) clock topic` | - | topic `clock` (BackendClock.cs:32) |  |
| `(new only) diagnostics activity/soak/drop` | - | `diagnostics/*` (DiagnosticsFeature.cs:25-28) |  |

### Desktop-only (GPUI-side, not backend)
| Old command / message | Old file:line | New socket call/topic | Note |
|---|---|---|---|
| `browser tab family (create/show/hide/navigate/reload/back/forward/close/bounds/viewport/picker/inspect/capture/devtools/external; 20 cmds)` | - | n/a | browser/mod.rs:1398-1538; event browser-tab-navigation browserBackend.ts:111 |
| `continue_app_exit / cancel_app_exit; event assembly_exit_requested` | - | n/a | main.rs:3297,3291; tauriSource.ts:918 |
| `open_source_file / reveal_source_file / open_path / reveal_path / move_to_trash (OS shell actions)` | - | n/a | files/commands.rs:202-229,386; the file reading half is a backend gap |
| `list_installed_font_families, read_proof_mode_label` | - | n/a | commands/application.rs:16 |
| `tray / window management` | - | n/a | no #[tauri::command]; in main.rs setup (assumed, not read) |

## 4. The missing ones, in the order a user hits them
1. Delete/close/rename/pin a session (`close_agent_conversation`, `delete_agent_conversation_session`, `update_agent_conversation_session_meta`): sessions only list and create (SessionsFeature.cs:31-43).
2. Per-project session list and projects (`list_projects`, `list_agent_sessions_for_project`, `set_session_project`): no project concept; `sessions/page` has no filter.
3. Session drafts (`agent_conversation_*_session_draft`): unsent text is lost on switch.
4. Attachment listing for a session (`read_agent_conversation_attachments`, `_selected_`, `discard_original`): only begin/chunk/commit/drop/read exist.
5. Add/connect/disconnect a remote machine (`connect_remote_assembly`, `install_remote_assembly`, `disconnect`, `remove profile`, statuses): `machines/list` only reads.
6. Terminals (all 6 session commands plus output streaming): nothing.
7. Git status/diff/stage/commit/push/pull/branches/stash (about 25 commands) and worktree list/remove/archive: nothing on the socket.
8. Pull requests (create, status, GitHub list/review/comment/merge, generate details/commit message): nothing.
9. Files: list/read/write/search tree, definitions/references, project root validation: nothing.
10. Language servers (LSP family, plus `/lsp` websocket): nothing.
11. Session workspace layout persistence (open tabs, expanded paths): nothing (may fit `preferences`, not designed).
12. Provider CLI check/update/install/restart (local and remote) and config probe/warm: nothing.
13. Agent messages, Build Board, main orchestrator, phone push: nothing on socket (only `/mcp` helper tools).
14. Workflows (19 commands, `workflow-run-changed` event): nothing.
15. Notion tasks and OAuth (20 commands): nothing.
16. Usage/cost history and current provider quota (11 commands): nothing.
17. Resource monitor (snapshot, disk scan, stop process tree, memory pressure; only `diagnostics/memory.sample` exists), evidence gallery, runtime/playwright session killing.
18. History import from native CLI transcripts, handoff, annotations, child (subagent) history, history download, AGUI projection: nothing.
19. Helper (LLM key) settings and jobs: nothing.
Caveats: partial rows (ensure, approvals mapped to `send/answer`, cancel) are assumed from names, not by comparing payloads.
