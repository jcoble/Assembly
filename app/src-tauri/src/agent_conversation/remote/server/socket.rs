use std::collections::{BTreeMap, HashMap, HashSet};
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};

use axum::extract::ws::{Message as AxumMessage, WebSocket, WebSocketUpgrade};
use axum::extract::State;
use axum::http::{HeaderMap, StatusCode};
use axum::response::IntoResponse;
use futures_util::{SinkExt, StreamExt};
use mcb_core::session_store::{HistoryDownloadRows, SessionStore};
use tokio::sync::{broadcast, mpsc};

use super::execute_server_command;
use super::super::{PendingAttachment, ServerState, HISTORY_DOWNLOAD_CHUNK_BYTES,
    MAX_SERVER_FRAME_BYTES, MAX_WIRE_FRAME_BYTES, MESSAGE_CHANGES, OUTBOUND_FRAME_CAPACITY,
    REPLAY_PAGE_BYTES};
use super::super::wire::{encode_history_chunk, encode_server_frame, ClientFrame, RemoteCommand, RemoteResponse, ServerFrame};

pub(in crate::agent_conversation::remote) async fn upgrade_remote_socket(
    State(state): State<ServerState>,
    headers: HeaderMap,
    upgrade: WebSocketUpgrade,
) -> impl IntoResponse {
    let Some(challenge) = headers.get(super::super::handshake::CHALLENGE_HEADER).and_then(|value| value.to_str().ok())
        .filter(|value| super::super::handshake::valid_challenge(value)).map(str::to_owned) else {
        return StatusCode::UPGRADE_REQUIRED.into_response();
    };
    upgrade
        .max_message_size(MAX_WIRE_FRAME_BYTES)
        .on_upgrade(move |socket| serve_remote_socket(socket, state, challenge))
        .into_response()
}

pub(in crate::agent_conversation::remote) async fn next_outbound_frame(outbound: &mut mpsc::Receiver<ServerFrame>, history: &mut mpsc::Receiver<ServerFrame>) -> Option<ServerFrame> {
    loop {
        tokio::select! {
            biased;
            frame = outbound.recv(), if !outbound.is_closed() || !outbound.is_empty() => { if frame.is_some() { return frame; } }
            frame = history.recv(), if !history.is_closed() || !history.is_empty() => { if frame.is_some() { return frame; } }
            else => return None,
        }
    }
}

pub(in crate::agent_conversation::remote) async fn stream_history_download(state: &ServerState, id: u64, owned_id: &str, mut before_sequence: Option<i64>,
    history_outbound: &mpsc::Sender<ServerFrame>) -> Result<(), String> {
    let store = state.manager.store_handle();
    let mut facts = store.history_download_facts(owned_id, before_sequence).map_err(|e| e.to_string())?;
    let snapshot = state.manager.snapshot_since(owned_id, Some(i64::MAX))?.ok_or("Remote session was not found")?;
    // A transcript still being imported is measured in its file's bytes, and its
    // older pages are read in here whenever the saved rows run out.
    let import = unread_transcript(&store, owned_id)?;
    let mut cutoff = import.map(|(_, cutoff)| cutoff);
    let total = import.map_or(facts.total_bytes, |(length, _)| length);
    let mut covered = match import {
        Some((length, cutoff)) if before_sequence.is_some() => length.saturating_sub(cutoff),
        Some(_) => 0,
        None => facts.covered_bytes,
    };
    let start = ServerFrame::HistoryDownloadStart { id, owned_id: owned_id.into(), low_sequence: facts.low_sequence,
        high_sequence: facts.high_sequence, row_count: facts.row_count, total_bytes: total, covered_bytes: covered, snapshot };
    send_history_frame(history_outbound, start).await?;
    loop {
        let rows = match (facts.low_sequence, facts.high_sequence) {
            (Some(low), Some(high)) => store.history_download_rows(owned_id, low, high, before_sequence, HISTORY_DOWNLOAD_CHUNK_BYTES, covered).map_err(|e| e.to_string())?,
            _ => HistoryDownloadRows { events: Vec::new(), covered_bytes: covered },
        };
        let Some(oldest) = rows.events.last().map(|row| row.seq) else {
            if cutoff.is_none() { break; }
            state.manager.extend_imported_session(owned_id, crate::agent_conversation::IMPORT_MAX_BYTES, crate::agent_conversation::IMPORT_MAX_RECORDS)?;
            let next = unread_transcript(&store, owned_id)?.map(|(_, cutoff)| cutoff);
            if next.is_some() && next == cutoff { return Err("The transcript import made no progress".into()); }
            cutoff = next;
            facts = store.history_download_facts(owned_id, before_sequence).map_err(|e| e.to_string())?;
            continue;
        };
        covered = match import { Some(_) => total.saturating_sub(cutoff.unwrap_or(0)), None => rows.covered_bytes };
        let events = rows.events.into_iter().map(crate::agent_conversation::manager::stored_event).collect::<Result<Vec<_>, _>>()?;
        send_history_frame(history_outbound, ServerFrame::HistoryDownloadChunk { id, owned_id: owned_id.into(), oldest_sequence: oldest,
            covered_bytes: covered, gzip_base64: encode_history_chunk(&events)? }).await?;
        before_sequence = Some(oldest);
    }
    history_outbound.send(ServerFrame::HistoryDownloadDone { id, owned_id: owned_id.into() }).await.map_err(|e| e.to_string())
}

/// A top-level transcript import that has not reached the start of its file: the file's size and where reading stopped.
/// A transcript that is gone has nothing more to give, so its saved rows download as any others do.
fn unread_transcript(store: &SessionStore, owned_id: &str) -> Result<Option<(u64, u64)>, String> {
    let Some(session) = store.get_session(owned_id).map_err(|e| e.to_string())? else { return Ok(None); };
    match crate::agent_conversation::history::optional_import_cursor(&session.extra_json)? {
        Some(cursor) if !cursor.reached_start && cursor.parent_owned_id.is_none() => Ok(std::fs::metadata(&cursor.transcript_path)
            .ok().map(|metadata| (metadata.len(), cursor.cutoff_offset))),
        _ => Ok(None),
    }
}

async fn send_history_frame(outbound: &mpsc::Sender<ServerFrame>, frame: ServerFrame) -> Result<(), String> {
    if serde_json::to_vec(&frame).map_err(|e| e.to_string())?.len() > MAX_SERVER_FRAME_BYTES {
        return Err("Remote response exceeds the 16 MiB transport limit".into());
    }
    outbound.send(frame).await.map_err(|e| e.to_string())?;
    Ok(())
}

async fn serve_remote_socket(mut socket: WebSocket, state: ServerState, challenge: String) {
    if !super::super::handshake::admit_desktop(&mut socket, &state.token, &challenge).await { return; }
    let (mut writer, mut reader) = socket.split();
    let (outbound, mut outgoing) = mpsc::channel::<ServerFrame>(OUTBOUND_FRAME_CAPACITY);
    let (history_outbound, mut history_outgoing) = mpsc::channel::<ServerFrame>(1);
    let restart_flushed = state.restart_flushed.clone();
    let writer_task = tokio::spawn(async move {
        while let Some(frame) = next_outbound_frame(&mut outgoing, &mut history_outgoing).await {
            let json = match encode_server_frame(&frame) {
                Ok(json) => json,
                Err(error) => {
                    eprintln!("Remote transport closed: {error}");
                    let _ = writer.send(AxumMessage::Close(None)).await;
                    break;
                }
            };
            let restarting = matches!(frame, ServerFrame::Response { response: RemoteResponse::Restarting, .. });
            let sent = writer.send(AxumMessage::Text(json.into())).await;
            if restarting {
                restart_flushed.notify_one();
            }
            if sent.is_err() { break; }
        }
    });
    let socket_id = state.relay.next_id.fetch_add(1, Ordering::Relaxed);
    state.relay.desktops.lock().unwrap_or_else(std::sync::PoisonError::into_inner).push((socket_id, outbound.clone()));
    let mut live_events = state.events.subscribe();
    let mut message_changes = MESSAGE_CHANGES.subscribe();
    message_changes.mark_unchanged();
    let mut live_terminals = state.terminals.output.subscribe();
    let mut queued = BTreeMap::<String, i64>::new();
    let (completed, mut completions) = mpsc::channel::<(u64, Result<Option<RemoteResponse>, String>)>(32);
    let upload = Arc::new(Mutex::new(None::<PendingAttachment>));
    let mut request_tasks = HashMap::new();
    let mut selected_child_watches = HashSet::<(String, u64)>::new();
    loop {
        let upload_deadline = upload.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
            .as_ref().map(|pending| pending.deadline);
        let message = tokio::select! {
            _ = tokio::time::sleep_until(upload_deadline.unwrap_or_else(tokio::time::Instant::now)), if upload_deadline.is_some() => {
                let mut pending = upload.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
                if pending.as_ref().is_some_and(|item| item.deadline <= tokio::time::Instant::now()) { *pending = None; }
                None
            }
            message = reader.next() => {
                let Some(Ok(message)) = message else { break; };
                Some(message)
            }
            Ok(()) = message_changes.changed() => {
                if outbound.send(ServerFrame::MessagesChanged).await.is_err() { break; }
                None
            }
            event = live_events.recv() => {
                match event {
                    Ok(event) => {
                        let cursor = queued.entry(event.owned_id.clone()).or_insert(i64::MIN);
                        if event.sequence > *cursor {
                            *cursor = event.sequence;
                            if outbound.send(ServerFrame::Event { event }).await.is_err() { break; }
                        }
                    }
                    Err(broadcast::error::RecvError::Lagged(_)) => {
                        // Replay only sessions known to this socket. A missing session's
                        // next live event skips past the client's copy, and the client asks for it.
                        let mut failed = false;
                        for owned_id in queued.keys().cloned().collect::<Vec<_>>() {
                            if let Err(error) = replay_session(&state, &outbound, &mut queued, &owned_id).await {
                                eprintln!("Remote lag replay failed: {error}");
                                failed = true;
                                break;
                            }
                        }
                        if failed { break; }
                    }
                    Err(broadcast::error::RecvError::Closed) => break,
                }
                None
            }
            frame = live_terminals.recv() => {
                match frame {
                    Ok(frame) => {
                        if outbound.send(ServerFrame::TerminalOutput { frame }).await.is_err() { break; }
                    }
                    // A socket too slow to keep up loses those chunks; reopening the tab replays scrollback.
                    Err(broadcast::error::RecvError::Lagged(_)) => {}
                    Err(broadcast::error::RecvError::Closed) => break,
                }
                None
            }
            completion = completions.recv() => {
                let Some((id, response)) = completion else { break; };
                request_tasks.remove(&id);
                let frame = match response {
                    Ok(Some(response)) => ServerFrame::Response { id, response },
                    Ok(None) => continue,
                    Err(message) => ServerFrame::Error { id, message },
                };
                if outbound.send(frame).await.is_err() { break; }
                None
            }
        };
        let Some(message) = message else {
            continue;
        };
        let AxumMessage::Text(text) = message else {
            continue;
        };
        let Ok(frame) = serde_json::from_str::<ClientFrame>(text.as_str()) else {
            continue;
        };
        match frame {
            ClientFrame::Authenticate { .. } => break,
            ClientFrame::Request { id, command } => {
                match &command {
                    RemoteCommand::SelectChildHistory {
                        parent_owned_id,
                        request_id,
                        ..
                    } => {
                        state
                            .manager
                            .reserve_child_history_request(parent_owned_id, *request_id);
                        selected_child_watches
                            .insert((parent_owned_id.clone(), *request_id));
                    }
                    RemoteCommand::StopChildHistory {
                        parent_owned_id,
                        request_id,
                    } => {
                        selected_child_watches
                            .remove(&(parent_owned_id.clone(), *request_id));
                        let stopped = state
                            .manager
                            .stop_child_history(parent_owned_id, *request_id);
                        let _ = outbound
                            .send(ServerFrame::Response {
                                id,
                                response: RemoteResponse::Bool(stopped),
                            })
                            .await;
                        continue;
                    }
                    _ => {}
                }
                let request_state = state.clone();
                let request_upload = upload.clone();
                let completion_sink = completed.clone();
                let progress_outbound = outbound.clone();
                let history_sink = history_outbound.clone();
                let task = tokio::spawn(async move {
                    if let RemoteCommand::DownloadHistory { owned_id, before_sequence } = &command {
                        let result = stream_history_download(&request_state, id, owned_id, *before_sequence, &history_sink).await.map(|()| None);
                        let _ = completion_sink.send((id, result)).await;
                        return;
                    }
                    let progress: crate::agent_conversation::providers::updates::ProgressSink = Arc::new(move |progress| {
                        let _ = progress_outbound.try_send(ServerFrame::ProviderDownloadProgress { id, progress });
                    });
                    let response = execute_server_command(&request_state, &request_upload, command, progress).await;
                    let _ = completion_sink.send((id, response.map(Some))).await;
                });
                if let Some(previous) = request_tasks.insert(id, task.abort_handle()) {
                    previous.abort();
                }
            }
            ClientFrame::Cancel { id } => {
                if let Some(task) = request_tasks.remove(&id) {
                    task.abort();
                }
                let cancelled = selected_child_watches
                    .iter()
                    .filter(|(_, request_id)| *request_id == id)
                    .cloned()
                    .collect::<Vec<_>>();
                for (parent_owned_id, request_id) in cancelled {
                    state.manager.stop_child_history(&parent_owned_id, request_id);
                    selected_child_watches.remove(&(parent_owned_id, request_id));
                }
            }
            ClientFrame::RelayReply { request_id, result } => {
                if let Some(reply) = state.relay.pending.lock().unwrap_or_else(std::sync::PoisonError::into_inner).remove(&request_id) {
                    let _ = reply.send(result);
                }
            }
            ClientFrame::Resume { cursors } => {
                let named = cursors.keys().cloned().collect::<Vec<_>>();
                queued.extend(cursors);
                let mut failed = false;
                for owned_id in named {
                    if let Err(error) = replay_session(&state, &outbound, &mut queued, &owned_id).await {
                        eprintln!("Remote resume replay failed: {error}");
                        failed = true;
                        break;
                    }
                }
                if failed { break; }
            }
        }
    }
    // The writer ends once every sender is gone, this one included.
    state.relay.desktops.lock().unwrap_or_else(std::sync::PoisonError::into_inner).retain(|(id, _)| *id != socket_id);
    for (_, task) in request_tasks.drain() {
        task.abort();
    }
    for (parent_owned_id, request_id) in selected_child_watches {
        state.manager.stop_child_history(&parent_owned_id, request_id);
    }
    drop(outbound);
    drop(history_outbound);
    let _ = writer_task.await;
}

pub(in crate::agent_conversation::remote) async fn replay_session(
    state: &ServerState,
    outbound: &mpsc::Sender<ServerFrame>,
    queued: &mut BTreeMap<String, i64>,
    owned_id: &str,
) -> Result<(), String> {
    let Some(snapshot) = state.manager.snapshot_since(owned_id, Some(i64::MAX))? else { return Ok(()); };
    outbound.send(ServerFrame::ReplayStart { owned_id: owned_id.into() }).await.map_err(|e| e.to_string())?;
    let mut sequence = *queued.get(owned_id).unwrap_or(&i64::MIN);
    loop {
        let page = state.manager.list_events_after(owned_id, sequence, REPLAY_PAGE_BYTES)?;
        if page.events.is_empty() { break; }
        let has_more = page.has_more;
        for event in page.events {
            sequence = event.sequence;
            outbound.send(ServerFrame::Event { event }).await.map_err(|error| error.to_string())?;
            queued.insert(owned_id.to_string(), sequence);
        }
        if !has_more { break; }
    }
    let head = snapshot.last_sequence;
    queued.insert(owned_id.into(), sequence.min(head));
    outbound.send(ServerFrame::ReplayComplete { owned_id: owned_id.into(), head, snapshot })
        .await.map_err(|e| e.to_string())?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_conversation::manager::AgentRuntimeManager;
    use crate::agent_conversation::protocol::AgentConversationProvider;
    use crate::agent_conversation::providers::ProviderRegistry;
    use super::super::super::server_state;
    use super::super::super::wire::decode_history_chunk;

    /// A Codex transcript of `count` user messages, each about `text_bytes` long.
    fn transcript(directory: &std::path::Path, count: usize, text_bytes: usize) -> std::path::PathBuf {
        let path = directory.join("rollout.jsonl");
        let text = "x".repeat(text_bytes);
        let meta = "{\"timestamp\":\"2026-10-20T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"native\",\"cwd\":\"/work\",\"thread_source\":\"user\",\"source\":\"cli\"}}\n";
        let lines: String = std::iter::once(meta.to_string()).chain((1..=count).map(|index| format!(
            "{{\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"id\":\"item-{index}\",\"role\":\"user\",\"content\":[{{\"type\":\"input_text\",\"text\":\"{text}\"}}]}}}}\n"
        ))).collect();
        std::fs::write(&path, lines).unwrap();
        path
    }

    fn manager(directory: &std::path::Path) -> AgentRuntimeManager {
        AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap()
    }

    /// Every frame of one download: Start's total and covered, each chunk's covered and sequences, and whether Done came.
    async fn download(state: ServerState, owned_id: String) -> (u64, u64, Vec<u64>, Vec<i64>) {
        let (sender, mut frames) = mpsc::channel(1);
        let producer = tokio::spawn(async move { stream_history_download(&state, 7, &owned_id, None, &sender).await });
        let Some(ServerFrame::HistoryDownloadStart { total_bytes, covered_bytes, .. }) = frames.recv().await else { panic!("no start") };
        let (mut covered, mut sequences) = (Vec::new(), Vec::new());
        loop {
            match frames.recv().await.expect("download ended without done") {
                ServerFrame::HistoryDownloadChunk { covered_bytes, gzip_base64, .. } => {
                    covered.push(covered_bytes);
                    sequences.extend(decode_history_chunk(&gzip_base64).unwrap().iter().map(|event| event.sequence));
                }
                ServerFrame::HistoryDownloadDone { id: 7, .. } => break,
                other => panic!("unexpected frame {other:?}"),
            }
        }
        producer.await.unwrap().unwrap();
        (total_bytes, covered_bytes, covered, sequences)
    }

    #[tokio::test]
    async fn history_download_imports_older_transcript_pages_while_it_streams() {
        let directory = std::env::temp_dir().join(format!("import-download-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        // About 5 MiB: the newest 2 MiB page is read when the session is named, the rest while streaming.
        let path = transcript(&directory, 250, 20_000);
        let length = std::fs::metadata(&path).unwrap().len();
        let manager = manager(&directory);
        let (owned_id, _) = manager.begin_import_transcript_session(AgentConversationProvider::Codex, "native",
            &path, &directory.to_string_lossy(), None, None).await.unwrap();
        manager.finish_import_transcript_session(&owned_id, crate::agent_conversation::IMPORT_MAX_BYTES,
            crate::agent_conversation::IMPORT_MAX_RECORDS).unwrap();
        let store = manager.store_handle();
        let state = server_state(manager, directory.clone(), Arc::from("test")).unwrap();
        let (total, start_covered, covered, sequences) = download(state, owned_id.clone()).await;

        assert_eq!((total, start_covered), (length, 0));
        assert!(covered.windows(2).all(|pair| pair[0] <= pair[1]), "{covered:?}");
        assert!(covered.first().is_some_and(|first| *first > 0));
        // The last read can find only the file's header; Done then stands for the rest.
        assert!(covered.last().is_some_and(|last| *last <= length && *last > length - 1_000), "{covered:?}");
        assert_eq!(sequences.len(), 250);
        assert!(sequences.windows(2).all(|pair| pair[1] == pair[0] - 1), "newest first without gaps");
        assert!(*sequences.last().unwrap() < 0, "older pages are numbered below the first page");
        let session = store.get_session(&owned_id).unwrap().unwrap();
        let cursor = crate::agent_conversation::history::optional_import_cursor(&session.extra_json).unwrap().unwrap();
        assert!(cursor.reached_start);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn history_download_of_a_finished_import_keeps_payload_sizing() {
        let directory = std::env::temp_dir().join(format!("plain-download-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let path = transcript(&directory, 3, 100);
        let manager = manager(&directory);
        let (owned_id, _) = manager.begin_import_transcript_session(AgentConversationProvider::Codex, "native",
            &path, &directory.to_string_lossy(), None, None).await.unwrap();
        manager.finish_import_transcript_session(&owned_id, crate::agent_conversation::IMPORT_MAX_BYTES,
            crate::agent_conversation::IMPORT_MAX_RECORDS).unwrap();
        // Reading back to the start leaves nothing to import, so the download is sized by its saved rows.
        assert!(manager.extend_imported_session(&owned_id, crate::agent_conversation::IMPORT_MAX_BYTES,
            crate::agent_conversation::IMPORT_MAX_RECORDS).unwrap().reached_start);
        let facts = manager.store_handle().history_download_facts(&owned_id, None).unwrap();
        let state = server_state(manager, directory.clone(), Arc::from("test")).unwrap();
        let (total, start_covered, covered, sequences) = download(state, owned_id).await;

        assert_eq!((total, start_covered), (facts.total_bytes, 0));
        assert_eq!(covered, [facts.total_bytes]);
        assert_eq!(sequences, [3, 2, 1]);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn begin_import_and_transcript_scan_run_on_the_server() {
        let directory = std::env::temp_dir().join(format!("remote-import-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let path = transcript(&directory, 3, 100);
        std::fs::create_dir(directory.join(".git")).unwrap(); std::fs::write(directory.join(".git/HEAD"), "ref: refs/heads/main").unwrap();
        std::fs::write(&path, std::fs::read_to_string(&path).unwrap().replace("\"/work\"", &serde_json::to_string(directory.to_str().unwrap()).unwrap())).unwrap();
        let manager = manager(&directory);
        let cwd = directory.to_string_lossy().into_owned();
        // A project of the machine's own for the same folder: the desktop's project still files the session there.
        manager.store_handle().insert_or_get_project(&crate::project_folders::new_project_row("local".into(),
            crate::project_folders::ProjectFolderInspection { root_path: cwd.clone(), title: "here".into(), repo_key: String::new(), is_git: false })).unwrap();
        let RemoteResponse::Imported { owned_id, .. } = super::super::execute_remote_command(&manager, &directory,
            RemoteCommand::BeginImport { provider: AgentConversationProvider::Codex, native_session_id: "native".into(),
                transcript_path: path.to_string_lossy().into(), cwd: cwd.clone(), title: Some("Past".into()), project_id: Some("p1".into()) },
            Arc::new(|_| {})).await.unwrap() else { panic!("wrong begin response") };
        let row = manager.store_handle().get_session(&owned_id).unwrap().unwrap();
        assert_eq!(row.project_id.as_deref(), Some("p1"));
        assert_eq!(row.title.as_deref(), Some("Past"));
        assert!(manager.store_handle().has_display_events(&owned_id).unwrap(), "the newest page is read on the server");
        let RemoteResponse::Transcripts { sessions: found, common_dirs } = super::super::execute_remote_command(&manager, &directory,
            RemoteCommand::ScanTranscripts { log_path: Some(path.to_string_lossy().into()), project_roots: vec![cwd.clone()] }, Arc::new(|_| {})).await.unwrap()
            else { panic!("wrong scan response") };
        assert_eq!(common_dirs.get(&cwd), Some(&directory.join(".git").to_string_lossy().into_owned()));
        assert!(!found.is_empty());
        assert!(found.iter().all(|record| record.log_path.as_deref() == Some(path.to_string_lossy().as_ref())));
        std::fs::remove_dir_all(directory).unwrap();
    }

    /// Imports `path` again over the wire and downloads it: the session, whether it was rebuilt, its sequences and each row's text, newest first.
    async fn import_again(manager: &AgentRuntimeManager, directory: &std::path::Path, path: &std::path::Path) -> (String, bool, Vec<i64>, Vec<String>) {
        let RemoteResponse::Imported { owned_id, rebuilt } = super::super::execute_remote_command(manager, directory,
            RemoteCommand::BeginImport { provider: AgentConversationProvider::Codex, native_session_id: "native".into(),
                transcript_path: path.to_string_lossy().into(), cwd: directory.to_string_lossy().into(), title: None, project_id: None },
            Arc::new(|_| {})).await.unwrap() else { panic!("wrong begin response") };
        let store = manager.store_handle();
        let state = server_state(manager.clone(), directory.to_path_buf(), Arc::from("test")).unwrap();
        let (_, _, _, sequences) = download(state, owned_id.clone()).await;
        let (low, high) = (*sequences.iter().min().unwrap(), *sequences.iter().max().unwrap());
        let texts = store.history_download_rows(&owned_id, low, high, None, 1 << 30, 0).unwrap().events.iter()
            .map(|row| ["cli 4", "cli 5"].into_iter().find(|text| row.payload_json.contains(text)).unwrap_or("old").to_string()).collect();
        (owned_id, rebuilt, sequences, texts)
    }

    /// A new directory holding a transcript of `count` turns of `text_bytes` each, and a manager that has imported its newest page.
    async fn imported(count: usize, text_bytes: usize) -> (std::path::PathBuf, std::path::PathBuf, AgentRuntimeManager, String) {
        let directory = std::env::temp_dir().join(format!("import-again-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let path = transcript(&directory, count, text_bytes);
        let manager = manager(&directory);
        let (owned_id, _) = manager.begin_import_transcript_session(AgentConversationProvider::Codex, "native",
            &path, &directory.to_string_lossy(), None, None).await.unwrap();
        manager.finish_import_transcript_session(&owned_id, crate::agent_conversation::IMPORT_MAX_BYTES,
            crate::agent_conversation::IMPORT_MAX_RECORDS).unwrap();
        (directory, path, manager, owned_id)
    }

    #[tokio::test]
    async fn importing_again_brings_in_turns_taken_outside_assembly_once() {
        use mcb_core::session_store::EventRow;
        for (started_here, cli_turns) in [(false, true), (true, true), (true, false)] {
            let (directory, path, manager, mut owned_id) = imported(3, 100).await;
            let store = manager.store_handle();
            manager.extend_imported_session(&owned_id, crate::agent_conversation::IMPORT_MAX_BYTES,
                crate::agent_conversation::IMPORT_MAX_RECORDS).unwrap();
            if started_here {
                // The same three turns in a session Assembly started, which has no import cursor.
                let rows = store.history_download_rows(&owned_id, 1, 3, None, 1 << 30, 0).unwrap().events;
                let mut row = store.get_session(&owned_id).unwrap().unwrap();
                store.delete_session(&owned_id).unwrap();
                row.owned_id = "started-here".into();
                row.extra_json = crate::agent_conversation::manager::imported_session_extra(AgentConversationProvider::Codex).unwrap();
                store.upsert_session(&row).unwrap();
                for event in rows { store.append_event(&EventRow { owned_id: row.owned_id.clone(), ..event }).unwrap(); }
                owned_id = row.owned_id;
            }
            let mut expected = vec!["old"; 3];
            if cli_turns {
                // Taken straight after the session's last turn here, so the file's time says nothing.
                let mut text = std::fs::read_to_string(&path).unwrap();
                for index in 4..=5 {
                    text.push_str(&format!("{{\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"id\":\"item-{index}\",\"role\":\"user\",\"content\":[{{\"type\":\"input_text\",\"text\":\"cli {index}\"}}]}}}}\n"));
                }
                std::fs::write(&path, text).unwrap();
                expected = vec!["cli 5", "cli 4", "old", "old", "old"];
            }
            let case = format!("started_here={started_here} cli_turns={cli_turns}");
            let (again, rebuilt, sequences, texts) = import_again(&manager, &directory, &path).await;
            assert_eq!((again.as_str(), rebuilt), (owned_id.as_str(), true), "{case}: the same session, read again from its transcript");
            assert!(manager.list_sessions().unwrap().iter().any(|row| row.owned_id == again && row.numbered_after == Some(3)), "{case}: listed with where it restarted");
            assert_eq!(texts, expected, "{case}");
            assert_eq!(sequences.len(), expected.len(), "{case}: {sequences:?}");
            assert!(sequences.windows(2).all(|pair| pair[1] == pair[0] - 1), "{case}: newest first without gaps {sequences:?}");
            assert!(*sequences.last().unwrap() > 3, "{case}: numbered on above the old head, which live delivery has passed {sequences:?}");
            // Nothing new since: the second import reads nothing again.
            let (_, rebuilt_twice, sequences_twice, texts_twice) = import_again(&manager, &directory, &path).await;
            assert_eq!((rebuilt_twice, sequences_twice, texts_twice), (false, sequences, texts), "{case}");
            std::fs::remove_dir_all(directory).unwrap();
        }
    }

    #[tokio::test]
    async fn a_partly_imported_session_saved_by_its_runtime_keeps_reading_older_pages() {
        let (directory, path, manager, owned_id) = imported(250, 20_000).await;
        // The runtime's own save of the row, which writes its metadata without the import cursor.
        manager.set_workflow_session_title(&owned_id, "Named").unwrap();
        let (again, rebuilt, sequences, _) = import_again(&manager, &directory, &path).await;
        assert_eq!((again, rebuilt), (owned_id, false));
        assert_eq!(sequences.len(), 250, "the older pages still came in");
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn an_unreadable_grown_transcript_keeps_the_saved_conversation() {
        use std::os::unix::fs::PermissionsExt;
        let (directory, path, manager, owned_id) = imported(3, 100).await;
        let saved = manager.store_handle().history_download_facts(&owned_id, None).unwrap().row_count;
        std::fs::write(&path, format!("{}{{}}\n", std::fs::read_to_string(&path).unwrap())).unwrap();
        std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o000)).unwrap();
        let again = manager.begin_import_transcript_session(AgentConversationProvider::Codex, "native", &path,
            &directory.to_string_lossy(), None, None).await;
        std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o600)).unwrap();
        assert!(again.is_err(), "{again:?}");
        assert_eq!(manager.store_handle().history_download_facts(&owned_id, None).unwrap().row_count, saved);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn importing_again_waits_for_the_session_lifecycle() {
        let (directory, path, manager, owned_id) = imported(3, 100).await;
        let lifecycle = manager.lifecycle_guard(&owned_id).await.unwrap();
        let importer = manager.clone();
        let cwd = directory.to_string_lossy().into_owned();
        let import = tokio::spawn(async move {
            importer.begin_import_transcript_session(AgentConversationProvider::Codex, "native", &path, &cwd, None, None).await });
        for _ in 0..10 { tokio::task::yield_now().await; }
        assert!(!import.is_finished(), "activation holds the guard, so the import waits");
        drop(lifecycle);
        assert_eq!(import.await.unwrap().unwrap().0, owned_id);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn a_download_of_an_import_whose_transcript_is_gone_sends_its_saved_rows() {
        let (directory, path, manager, owned_id) = imported(250, 20_000).await;
        let saved = manager.store_handle().history_download_facts(&owned_id, None).unwrap().row_count;
        std::fs::remove_file(&path).unwrap();
        let state = server_state(manager, directory.clone(), Arc::from("test")).unwrap();
        let (_, _, _, sequences) = download(state, owned_id).await;
        assert_eq!(u64::try_from(sequences.len()).unwrap(), saved);
        std::fs::remove_dir_all(directory).unwrap();
    }
}
