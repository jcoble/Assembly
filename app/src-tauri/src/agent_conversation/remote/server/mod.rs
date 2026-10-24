pub(super) mod socket;

use std::future::IntoFuture;
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::routing::get;
use axum::Router;
use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};

use self::socket::upgrade_remote_socket;
use super::{server_state, PendingAttachment, ServerState, ATTACHMENT_CHUNK_BYTES};
use super::wire::{RemoteCommand, RemoteResponse};
use crate::agent_conversation::attachments;
use mcb_core::scanners::sessions::AgentSessionRecord;
use crate::agent_conversation::manager::AgentRuntimeManager;
use crate::agent_conversation::prompt_content::prompt_from_blocks;
use crate::agent_conversation::protocol::ExecutionEnvironment;
use crate::agent_conversation::providers::ProviderRegistry;
use crate::terminal::{self, TerminalKind};

/// Takes the backend token out of the environment before any child process starts.
fn take_server_token() -> Result<String, String> {
    let token = std::env::var("ASSEMBLY_SERVER_TOKEN");
    std::env::remove_var("ASSEMBLY_SERVER_TOKEN");
    token.map_err(|_| "ASSEMBLY_SERVER_TOKEN is required".to_string())
}

/// Runs the resident remote server instead of starting a Tauri window.
pub fn run_server_from_environment() -> Result<(), String> {
    let bind =
        std::env::var("ASSEMBLY_SERVER_BIND").unwrap_or_else(|_| "127.0.0.1:7777".to_string());
    let token = take_server_token()?;
    if token.len() < 32 {
        return Err("ASSEMBLY_SERVER_TOKEN must contain at least 32 characters".to_string());
    }
    let data_dir = std::env::var_os("ASSEMBLY_SERVER_DATA_DIR")
        .map(PathBuf::from)
        .ok_or_else(|| "ASSEMBLY_SERVER_DATA_DIR is required".to_string())?;
    std::fs::create_dir_all(&data_dir).map_err(|error| error.to_string())?;
    for entry in std::fs::read_dir(&data_dir).map_err(|error| error.to_string())? {
        let entry = entry.map_err(|error| error.to_string())?;
        let name = entry.file_name();
        if entry.file_type().map_err(|error| error.to_string())?.is_file()
            && name.to_string_lossy().strip_prefix(".attachment-upload-")
                .is_some_and(|id| uuid::Uuid::parse_str(id).is_ok()) {
            std::fs::remove_file(entry.path()).map_err(|error| error.to_string())?;
        }
    }
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .map_err(|error| error.to_string())?;
    runtime.block_on(async move {
        let manager = AgentRuntimeManager::open(
            ProviderRegistry::bundled_from_environment_at(Some(&data_dir))?,
            &data_dir.join("sessions.db"),
        )?;
        manager.start_closing_finished_helpers();
        let state = server_state(manager, data_dir.clone(), Arc::from(token))?;
        // This machine's sessions reach other machines through a connected desktop.
        crate::agent_conversation::messaging::endpoint()?.set_relay(state.relay.relay());
        crate::agent_conversation::messaging::endpoint()?.set_workflow(state.workflow.clone());
        let restart_requested = state.restart_requested.clone();
        let restart_flushed = state.restart_flushed.clone();
        let restart_manager = state.manager.clone();
        let listener = bind_listeners(&bind, std::env::var("ASSEMBLY_PHONE_BIND").ok().as_deref(), &state).await?;
        let app = Router::new()
            .route("/assembly", get(upgrade_remote_socket))
            .with_state(state);
        println!("Assembly remote server listening on {bind}");
        use axum::serve::ListenerExt;
        tokio::select! {
            result = axum::serve(listener.tap_io(|stream| {
                if let Err(error) = stream.set_nodelay(true) {
                    eprintln!("Could not disable remote socket packet delay: {error}");
                }
            }), app).into_future() => result.map_err(|error| error.to_string()),
            _ = wait_for_provider_restart(&restart_requested, &restart_flushed) => {
                // A restart ends running sessions: stop their adapters and
                // record the interrupted turns before exiting. A stuck adapter
                // must not keep the server from exiting, as on desktop quit.
                if tokio::time::timeout(Duration::from_secs(5), restart_manager.shutdown()).await.is_err() {
                    eprintln!("Adapter shutdown timed out after 5 seconds; restarting anyway");
                }
                // The service uses Restart=on-failure. The server owns the
                // accepted restart even if its requesting socket disappears.
                std::process::exit(75);
            }
        }
    })
}

/// Binds the desktop listener, then starts the optional phone listener (`phone.env`:
/// `/phone/v1` routes only, never `/assembly`). The desktop binds first so a phone
/// address that overlaps it is the one that is skipped, never the desktop.
pub(in crate::agent_conversation) async fn bind_listeners(bind: &str, phone_bind: Option<&str>, state: &ServerState) -> Result<tokio::net::TcpListener, String> {
    let listener = tokio::net::TcpListener::bind(bind).await.map_err(|error| error.to_string())?;
    crate::agent_conversation::phone::start_listener(phone_bind, state.clone()).await;
    Ok(listener)
}

pub(super) async fn wait_for_provider_restart(requested: &tokio::sync::Notify, flushed: &tokio::sync::Notify) {
    requested.notified().await;
    // Allow the acknowledgement to flush, but a dropped or stalled socket
    // must not strand the server in its no-new-requests state.
    let _ = tokio::time::timeout(Duration::from_secs(2), flushed.notified()).await;
}

pub(super) fn abort_attachment_upload(upload: &Mutex<Option<PendingAttachment>>, upload_id: &str) {
    let mut pending = upload.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    if pending.as_ref().is_some_and(|current| current.upload_id == upload_id) { *pending = None; }
}

pub(super) fn write_attachment_chunk(
    data_dir: &std::path::Path,
    upload: &Mutex<Option<PendingAttachment>>,
    upload_id: String,
    owned_id: String,
    mime_type: String,
    first: bool,
    last: bool,
    bytes: Vec<u8>,
) -> Result<Option<PendingAttachment>, String> {
    uuid::Uuid::parse_str(&upload_id).map_err(|_| "Attachment upload id is invalid")?;
    let mut pending = upload.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    if pending.as_ref().is_some_and(|current| current.upload_id != upload_id) {
        return Err("Another attachment transfer is active".into());
    }
    if bytes.is_empty() || bytes.len() > ATTACHMENT_CHUNK_BYTES {
        if pending.as_ref().is_some_and(|current| current.upload_id == upload_id) { *pending = None; }
        return Err("Attachment chunk is outside the transfer limit".into());
    }
    if first {
        if pending.is_some() { return Err("Attachment transfer already started".into()); }
        let path = data_dir.join(format!(".attachment-upload-{upload_id}"));
        let file = std::fs::OpenOptions::new().write(true).create_new(true).open(&path)
            .map_err(|error| format!("Could not start attachment transfer: {error}"))?;
        *pending = Some(PendingAttachment { upload_id: upload_id.clone(), owned_id: owned_id.clone(), mime_type: mime_type.clone(), path, file, bytes: 0, deadline: tokio::time::Instant::now() + Duration::from_secs(30) });
    }
    let Some(current) = pending.as_mut() else { return Err("Attachment transfer has not started".into()); };
    if current.owned_id != owned_id || current.mime_type != mime_type || current.bytes + bytes.len() > attachments::MAX_ATTACHMENT_BYTES {
        *pending = None;
        return Err("Attachment transfer is invalid or exceeds 20 MB".into());
    }
    use std::io::Write;
    if let Err(error) = current.file.write_all(&bytes) {
        *pending = None;
        return Err(format!("Could not write attachment transfer: {error}"));
    }
    current.bytes += bytes.len();
    current.deadline = tokio::time::Instant::now() + Duration::from_secs(30);
    Ok(if last { pending.take() } else { None })
}

pub(super) async fn execute_server_command(state: &ServerState, upload: &Arc<Mutex<Option<PendingAttachment>>>, command: RemoteCommand, progress: crate::agent_conversation::providers::updates::ProgressSink) -> Result<RemoteResponse, String> {
    command.refuse_in_proof_mode()?;
    let vault = state.data_dir.join("conversation-attachments");
    let command = match command {
        RemoteCommand::AttachmentChunk { upload_id, owned_id, mime_type, first, last, bytes } => {
            let bytes = tokio::task::spawn_blocking(move || BASE64.decode(bytes).map_err(|error| error.to_string()))
                .await.map_err(|error| error.to_string())??;
            let data_dir = state.data_dir.clone();
            let upload = upload.clone();
            let complete = tokio::task::spawn_blocking(move ||
                write_attachment_chunk(&data_dir, &upload, upload_id, owned_id, mime_type, first, last, bytes)
            ).await.map_err(|error| error.to_string())??;
            return match complete {
                Some(complete) => {
                    let store = state.manager.store_handle();
                    tokio::task::spawn_blocking(move || {
                        use std::io::Read;
                        let file = std::fs::File::open(&complete.path).map_err(|error| error.to_string())?;
                        let mut bytes = Vec::new();
                        file.take(attachments::MAX_ATTACHMENT_BYTES as u64 + 1).read_to_end(&mut bytes)
                            .map_err(|error| error.to_string())?;
                        if bytes.len() > attachments::MAX_ATTACHMENT_BYTES { return Err("Attachment exceeds 20 MB".into()); }
                        attachments::save_at(&vault, &store, &complete.owned_id, &complete.mime_type, &bytes).map(RemoteResponse::Attachment)
                    }).await.map_err(|error| error.to_string())?
                }
                None => Ok(RemoteResponse::Empty),
            };
        }
        RemoteCommand::AbortAttachmentUpload { upload_id } => {
            let upload = upload.clone();
            tokio::task::spawn_blocking(move || abort_attachment_upload(&upload, &upload_id))
                .await.map_err(|error| error.to_string())?;
            return Ok(RemoteResponse::Empty);
        }
        RemoteCommand::ReadAttachments { owned_id } => {
            let store = state.manager.store_handle();
            return tokio::task::spawn_blocking(move ||
                attachments::read_at(&vault, &store, &owned_id).map(RemoteResponse::Attachments)
            ).await.map_err(|error| error.to_string())?;
        }
        RemoteCommand::ReadSelectedAttachments {
            owned_id,
            attachment_ids,
        } => {
            let store = state.manager.store_handle();
            return tokio::task::spawn_blocking(move ||
                attachments::read_selected_at(&vault, &store, &owned_id, &attachment_ids)
                    .map(RemoteResponse::Attachments)
            )
            .await
            .map_err(|error| error.to_string())?;
        }
        RemoteCommand::ReadAttachmentChunk { owned_id, attachment_id, thumbnail, offset } => {
            let store = state.manager.store_handle();
            return tokio::task::spawn_blocking(move || {
                attachments::read_chunk_at(&vault, &store, &owned_id, &attachment_id, thumbnail, offset, ATTACHMENT_CHUNK_BYTES)
                    .map(|bytes| RemoteResponse::Bytes(BASE64.encode(bytes)))
            }).await.map_err(|error| error.to_string())?;
        }
        RemoteCommand::DeleteAttachment(request) => {
            let store = state.manager.store_handle();
            return tokio::task::spawn_blocking(move ||
                attachments::delete_at(&vault, &store, request).map(|_| RemoteResponse::Empty)
            ).await.map_err(|error| error.to_string())?;
        }
        RemoteCommand::TerminalStart(request) => {
            // Runs inline, like the Mac's own start, so a cancel that arrives first starts nothing.
            let terminals = &state.terminals;
            // An exited shell is never reused, so drop this session's before starting a new one.
            if let Some(owned_id) = request.owned_id.as_deref() {
                for info in terminal::list_terminal_sessions(&terminals.registry)? {
                    if info.kind == TerminalKind::UserPty && info.exited && info.owned_id.as_deref() == Some(owned_id) {
                        terminal::close_terminal_session(&terminals.registry, &info.session_id)?;
                    }
                }
            }
            return terminal::start_terminal_session(&terminals.registry, terminals.streams.clone(), request)
                .map(RemoteResponse::TerminalSession);
        }
        RemoteCommand::TerminalWrite { session_id, data } => {
            return terminal::write_terminal_session(&state.terminals.registry, &session_id, &data).map(RemoteResponse::Bool);
        }
        RemoteCommand::TerminalResize { session_id, cols, rows } => {
            return terminal::resize_terminal_session(&state.terminals.registry, &session_id, cols, rows).map(RemoteResponse::Bool);
        }
        RemoteCommand::TerminalScrollback { session_id, max_bytes } => {
            return terminal::read_terminal_session_scrollback(&state.terminals.registry, &session_id, max_bytes)
                .map(RemoteResponse::TerminalScrollback);
        }
        RemoteCommand::TerminalClose { session_id } => {
            return terminal::close_terminal_session(&state.terminals.registry, &session_id).map(RemoteResponse::Bool);
        }
        RemoteCommand::Delete { owned_id } => {
            // Close the session's shells first, so a failure leaves the session there to delete again.
            state.terminals.registry.close_user_terminals_for_owned(&owned_id)?;
            RemoteCommand::Delete { owned_id }
        }
        other => other,
    };
    if matches!(command, RemoteCommand::RestartForProviderUpdates) {
        let _exclusive = state.maintenance.write().await;
        if state.restarting.swap(true, Ordering::AcqRel) {
            return Err("The remote server is already restarting".into());
        }
        state.restart_requested.notify_one();
        return Ok(RemoteResponse::Restarting);
    }
    let _request = state.maintenance.read().await;
    if state.restarting.load(Ordering::Acquire) {
        return Err("The remote server is restarting; reconnect before continuing".into());
    }
    if let RemoteCommand::Workflow { operation, args } = command {
        // Phone pairing needs this server's pairing code and database, not the engine.
        if let Some(answer) = crate::agent_conversation::phone::operation(state, &operation, &args) {
            return answer.map(RemoteResponse::Workflow);
        }
        // The Workflows screen's costs come from this machine's usage rows, not the engine.
        if operation == "usage" {
            let (path, run_id) = (state.data_dir.join("usage-history.sqlite3"), args["runId"].as_str().map(str::to_string));
            let rows = tokio::task::spawn_blocking(move || crate::usage_db::UsageDb::open(path)?.read_workflow_usage(run_id.as_deref()))
                .await.map_err(|error| error.to_string())??;
            return serde_json::to_value(rows).map(RemoteResponse::Workflow).map_err(|error| error.to_string());
        }
        // A dropped socket or a client cancel aborts this request's task; the engine
        // call runs on its own task so a run is never left half-started.
        let workflow = state.workflow.clone();
        return tokio::spawn(async move { crate::workflow::execute_operation(&workflow, &operation, args).await })
            .await.map_err(|error| error.to_string())?.map(RemoteResponse::Workflow);
    }
    execute_remote_command(&state.manager, &vault, command, progress).await
}

/// Runs one command for the phone listener exactly as the desktop socket runs it: the
/// same restart guard, workflow task and handlers. The phone API builds each command
/// itself from the few actions phone spec §5.3 allows; it never forwards one from the phone.
pub(in crate::agent_conversation) async fn execute_phone_command(state: &ServerState, command: RemoteCommand) -> Result<RemoteResponse, String> {
    execute_server_command(state, &Arc::default(), command, Arc::new(|_| {})).await
}

pub(super) async fn execute_remote_command(
    manager: &AgentRuntimeManager,
    vault: &std::path::Path,
    command: RemoteCommand,
    progress: crate::agent_conversation::providers::updates::ProgressSink,
) -> Result<RemoteResponse, String> {
    match command {
        RemoteCommand::RestartForProviderUpdates => Err("Remote restart requires exclusive request ownership".into()),
        RemoteCommand::Workflow { .. } => Err("Workflow requests need the server's workflow engine".into()),
        RemoteCommand::Messaging { op, args } => crate::agent_conversation::messaging::answer(manager.store(), Some(manager), &op, &args)
            .map(RemoteResponse::Messaging),
        RemoteCommand::Workspace { operation, args } => super::workspace::execute(operation, args).await.map(RemoteResponse::Workspace),
        RemoteCommand::ProbeProviderConfig { provider, cwd } => manager
            .probe_provider_config(provider, &cwd)
            .await
            .map(RemoteResponse::Config),
        command @ (RemoteCommand::CheckProviderUpdates | RemoteCommand::InstallProviderUpdates { .. }) => {
            let data_dir = std::env::var_os("ASSEMBLY_SERVER_DATA_DIR").map(PathBuf::from)
                .ok_or_else(|| "Remote server data directory is unavailable".to_string())?;
            let status = if let RemoteCommand::InstallProviderUpdates { provider } = command {
                // Installs land in a new folder; running adapters keep theirs until restart.
                crate::agent_conversation::providers::updates::install_at(&data_dir, manager.providers(), &provider, progress).await?
            } else {
                crate::agent_conversation::providers::updates::check_at(&data_dir, manager.providers()).await?
            };
            Ok(RemoteResponse::ProviderUpdates(status))
        }
        RemoteCommand::ListSessions => {
            let mut sessions = manager.list_sessions()?;
            for session in &mut sessions {
                session.execution_environment = ExecutionEnvironment::Remote;
            }
            Ok(RemoteResponse::Sessions(sessions))
        }
        RemoteCommand::Ensure(mut request) => {
            request.execution_environment = ExecutionEnvironment::Local;
            manager
                .ensure_async(request)
                .await
                .map(RemoteResponse::Connection)
        }
        // Request ownership is per WebSocket, not shared across Mac clients.
        RemoteCommand::Snapshot {
            owned_id,
            request_id: _,
            after_sequence,
        } => manager
            .snapshot_since(&owned_id, after_sequence)
            .map(RemoteResponse::Snapshot),
        RemoteCommand::ExtendImport { owned_id } => {
            let progress = manager.extend_imported_session(&owned_id, crate::agent_conversation::IMPORT_MAX_BYTES, crate::agent_conversation::IMPORT_MAX_RECORDS)?;
            Ok(RemoteResponse::ImportProgress { added: progress.added, reached_start: progress.reached_start })
        }
        RemoteCommand::ScanTranscripts { log_path, project_roots } => tokio::task::spawn_blocking(move || {
            let sessions = match log_path {
                Some(path) => mcb_core::scanners::sessions::scan_session_details(&path),
                // The desktop keeps no turn text from the full list, so it is not sent.
                None => mcb_core::scanners::sessions::scan_sessions().into_iter()
                    .map(|session| AgentSessionRecord { latest_turns: Vec::new(), ..session }).collect(),
            };
            let folders: std::collections::BTreeSet<_> = sessions.iter().filter_map(|session| session.project_path.as_deref())
                .chain(project_roots.iter().map(String::as_str)).collect();
            let common_dirs = folders.into_iter().filter_map(|folder| {
                Some((folder.to_string(), crate::project_folders::git_common_dir(folder)?))
            }).collect();
            RemoteResponse::Transcripts { sessions, common_dirs }
        }).await.map_err(|error| error.to_string()),
        RemoteCommand::BeginImport { provider, native_session_id, transcript_path, cwd, title, project_id } => {
            // Filed under this Mac's project for the folder, which is how its rail groups it, even if the machine has its own.
            let (owned_id, rebuilt) = manager.begin_import_transcript_session(provider, &native_session_id,
                std::path::Path::new(&transcript_path), &cwd, title, project_id).await?;
            // The rest of the transcript is read in by the history download, as it streams.
            manager.finish_import_transcript_session(&owned_id, crate::agent_conversation::IMPORT_MAX_BYTES, crate::agent_conversation::IMPORT_MAX_RECORDS)?;
            Ok(RemoteResponse::Imported { owned_id, rebuilt })
        }
        RemoteCommand::SelectChildHistory {
            parent_owned_id,
            child_session_id,
            request_id,
        } => manager
            .select_reserved_child_history(
                &parent_owned_id,
                &child_session_id,
                request_id,
                crate::agent_conversation::IMPORT_MAX_BYTES as u32,
                crate::agent_conversation::IMPORT_MAX_BYTES,
                crate::agent_conversation::IMPORT_MAX_RECORDS,
            )
            .map(|selection| RemoteResponse::ChildHistoryId(selection.history_owned_id)),
        RemoteCommand::StopChildHistory {
            parent_owned_id,
            request_id,
        } => Ok(RemoteResponse::Bool(
            manager.stop_child_history(&parent_owned_id, request_id),
        )),
        RemoteCommand::DownloadHistory { .. } => Err("History download requires the stream boundary".into()),
        RemoteCommand::EventsAfter {
            owned_id,
            after_sequence,
            max_bytes,
        } => manager
            .list_events_after(&owned_id, after_sequence, max_bytes)
            .map(RemoteResponse::EventPage),
        RemoteCommand::Capabilities { owned_id } => manager
            .capabilities_for_owned_id(&owned_id)
            .map(RemoteResponse::Capabilities),
        RemoteCommand::Config { owned_id } => manager
            .conversation_config(&owned_id)
            .map(RemoteResponse::Config),
        RemoteCommand::WarmConfig {
            owned_id,
            generation,
        } => manager
            .warm_conversation_config(&owned_id, generation)
            .await
            .map(RemoteResponse::Config),
        RemoteCommand::SetConfig(request) => manager
            .set_conversation_config(request)
            .await
            .map(RemoteResponse::Config),
        RemoteCommand::SetConfigOption(request) => manager
            .set_config(
                &request.owned_id,
                request.generation,
                &request.option_id,
                request.value,
            )
            .await
            .map(RemoteResponse::ConfigOptions),
        RemoteCommand::SetDraft { owned_id, text } => {
            manager.set_session_draft(&owned_id, &text)?;
            Ok(RemoteResponse::Empty)
        }
        RemoteCommand::GetDraft { owned_id } => manager
            .get_session_draft(&owned_id)
            .map(RemoteResponse::OptionalString),
        RemoteCommand::ClearDraft { owned_id } => {
            manager.clear_session_draft(&owned_id)?;
            Ok(RemoteResponse::Empty)
        }
        RemoteCommand::WriteWorkspace {
            owned_id,
            snapshot_json,
        } => {
            manager.write_workspace(&owned_id, &snapshot_json)?;
            Ok(RemoteResponse::Empty)
        }
        RemoteCommand::ReadWorkspace { owned_id } => manager
            .read_workspace(&owned_id)
            .map(RemoteResponse::OptionalString),
        RemoteCommand::ReadExpandedPaths { owned_id, root } => manager
            .read_workspace_expanded_paths(&owned_id, &root)
            .map(RemoteResponse::Strings),
        RemoteCommand::WriteExpandedPaths {
            owned_id,
            root,
            paths,
        } => {
            manager.write_workspace_expanded_paths(&owned_id, &root, &paths)?;
            Ok(RemoteResponse::Empty)
        }
        RemoteCommand::DeleteWorkspace { owned_id } => {
            manager.delete_workspace(&owned_id)?;
            Ok(RemoteResponse::Empty)
        }
        RemoteCommand::ChangeCheckout(request) => {
            manager.change_checkout(request).await.map(|mut session| {
                session.execution_environment = ExecutionEnvironment::Remote;
                RemoteResponse::Session(session)
            })
        }
        RemoteCommand::UpdateMeta(request) => {
            manager.update_session_meta(request).map(|mut session| {
                session.execution_environment = ExecutionEnvironment::Remote;
                RemoteResponse::Session(session)
            })
        }
        RemoteCommand::Close {
            owned_id,
            generation,
        } => manager
            .close(&owned_id, generation)
            .await
            .map(RemoteResponse::Bool),
        RemoteCommand::Delete { owned_id } => {
            manager.delete(&owned_id).await.map(RemoteResponse::Bool)
        }
        RemoteCommand::AttachmentChunk { .. } | RemoteCommand::AbortAttachmentUpload { .. }
        | RemoteCommand::ReadAttachments { .. } | RemoteCommand::ReadSelectedAttachments { .. }
        | RemoteCommand::ReadAttachmentChunk { .. }
        | RemoteCommand::DeleteAttachment(_) => Err("Attachment command needs socket ownership".into()),
        RemoteCommand::Send(request) => {
            if request.content.iter().any(|block| matches!(block, crate::agent_conversation::prompt_content::AgentPromptContentBlock::Image { .. })) {
                return Err("Remote images must use saved attachment ids".into());
            }
            let mut prompt = if request.text.trim().is_empty() {
                crate::agent_conversation::providers::AgentPrompt { text: String::new(), images: Vec::new(), attachment_ids: Vec::new() }
            } else {
                prompt_from_blocks(request.text.trim(), request.content)?
            };
            if request.attachment_ids.is_empty() && prompt.text.is_empty() {
                return Err("Message cannot be empty".into());
            }
            if !request.attachment_ids.is_empty() {
                let store = manager.store_handle();
                let vault = vault.to_path_buf();
                let owned_id = request.owned_id.clone();
                let ids = request.attachment_ids.clone();
                prompt.images = tokio::task::spawn_blocking(move || {
                    let rows = store.get_attachments(&owned_id, &ids).map_err(|error| error.to_string())?;
                    let mut total_image_bytes = 0usize;
                    let mut images = Vec::new();
                    for id in &ids {
                        let row = rows.iter().find(|row| &row.id == id)
                            .ok_or_else(|| "Attachment does not belong to this session".to_string())?;
                        let bytes = attachments::read_original_at(&vault, row)?;
                        total_image_bytes += bytes.len();
                        if total_image_bytes > attachments::MAX_ATTACHMENT_BYTES {
                            return Err("Remote prompt images exceed 20 MB".into());
                        }
                        images.push(prompt_from_blocks("", vec![crate::agent_conversation::prompt_content::AgentPromptContentBlock::Image {
                            mime_type: row.mime_type.clone(), data: bytes, name: Some(row.file_name.clone()),
                        }])?.images.remove(0));
                    }
                    Ok::<_, String>(images)
                }).await.map_err(|error| error.to_string())??;
            }
            prompt.attachment_ids = request.attachment_ids;
            let receipt = manager
                .send_message(
                    &request.owned_id,
                    request.generation,
                    prompt,
                    request.model,
                    request.approval_policy,
                )
                .await?;
            Ok(RemoteResponse::SendReceipt(receipt))
        }
        RemoteCommand::RespondApproval(request) => {
            manager
                .respond_legacy_approval(
                    &request.owned_id,
                    request.generation,
                    request.request_id,
                    request.decision,
                )
                .await?;
            Ok(RemoteResponse::Empty)
        }
        RemoteCommand::RespondPermission(request) => {
            manager
                .respond_permission_option(
                    &request.owned_id,
                    request.generation,
                    request.request_id,
                    request.option_id,
                )
                .await?;
            Ok(RemoteResponse::Empty)
        }
        RemoteCommand::RespondInput(request) => {
            manager
                .respond_user_input(crate::agent_conversation::protocol::AgentUserInputResponse {
                    identity: crate::agent_conversation::protocol::AgentRequestIdentity {
                        owned_id: request.owned_id,
                        generation: request.generation,
                        request_id: request.request_id,
                        turn_id: None,
                        item_id: None,
                    },
                    action: request.action,
                    content: request.content,
                })
                .await?;
            Ok(RemoteResponse::Empty)
        }
        RemoteCommand::Stop(request) => {
            manager
                .cancel_turn(&request.owned_id, request.generation)
                .await?;
            Ok(RemoteResponse::Empty)
        }
        RemoteCommand::TerminalStart(_) | RemoteCommand::TerminalWrite { .. } | RemoteCommand::TerminalResize { .. }
        | RemoteCommand::TerminalScrollback { .. } | RemoteCommand::TerminalClose { .. } => {
            Err("Terminal command needs the server's terminals".into())
        }
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn server_token_is_removed_from_the_environment_after_reading() {
        std::env::set_var("ASSEMBLY_SERVER_TOKEN", "test-token");
        assert_eq!(super::take_server_token().unwrap(), "test-token");
        assert!(std::env::var_os("ASSEMBLY_SERVER_TOKEN").is_none());
    }
}
