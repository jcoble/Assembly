use crate::agent_conversation::remote::{
    split_remote_terminal_id, split_remote_workspace_path, RemoteConnectionManager,
};
use crate::projection_streams::ProjectionStreams;
use crate::terminal::{
    self, TerminalRegistry, TerminalScrollbackSnapshot, TerminalSessionInfo, TerminalStartRequest,
};

// A terminal for a remote session runs on that session's machine. Its folder arrives as
// `assembly-remote://<machine>/<path>` and its id as `assembly-remote-terminal://<machine>/<id>`.

#[tauri::command]
pub(crate) async fn start_terminal_session(
    terminal_registry: tauri::State<'_, TerminalRegistry>,
    projection_streams: tauri::State<'_, ProjectionStreams>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    mut request: TerminalStartRequest,
    request_id: Option<u64>,
) -> Result<TerminalSessionInfo, String> {
    if let Some((profile_id, path)) = split_remote_workspace_path(&request.cwd) {
        let profile_id = profile_id.to_string();
        request.cwd = path.to_string();
        return remote.start_terminal(&profile_id, request, request_id).await;
    }
    terminal::start_terminal_session(
        &terminal_registry,
        projection_streams.inner().clone(),
        request,
    )
}

#[tauri::command]
pub(crate) async fn list_terminal_sessions(
    terminal_registry: tauri::State<'_, TerminalRegistry>,
) -> Result<Vec<TerminalSessionInfo>, String> {
    terminal::list_terminal_sessions(&terminal_registry)
}

#[tauri::command]
pub(crate) async fn read_terminal_session_scrollback(
    terminal_registry: tauri::State<'_, TerminalRegistry>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    session_id: String,
    max_bytes: Option<usize>,
) -> Result<Option<TerminalScrollbackSnapshot>, String> {
    if split_remote_terminal_id(&session_id).is_some() {
        return remote.read_terminal_scrollback(&session_id, max_bytes).await;
    }
    terminal::read_terminal_session_scrollback(&terminal_registry, &session_id, max_bytes)
}

#[tauri::command]
pub(crate) async fn write_terminal_session(
    terminal_registry: tauri::State<'_, TerminalRegistry>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    session_id: String,
    data: String,
) -> Result<bool, String> {
    if split_remote_terminal_id(&session_id).is_some() {
        return remote.write_terminal(&session_id, data).await;
    }
    terminal::write_terminal_session(&terminal_registry, &session_id, &data)
}

#[tauri::command]
pub(crate) async fn resize_terminal_session(
    terminal_registry: tauri::State<'_, TerminalRegistry>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    session_id: String,
    cols: Option<u16>,
    rows: Option<u16>,
) -> Result<bool, String> {
    if split_remote_terminal_id(&session_id).is_some() {
        return remote.resize_terminal(&session_id, cols, rows).await;
    }
    terminal::resize_terminal_session(&terminal_registry, &session_id, cols, rows)
}

#[tauri::command]
pub(crate) async fn close_terminal_session(
    terminal_registry: tauri::State<'_, TerminalRegistry>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    session_id: String,
) -> Result<bool, String> {
    if split_remote_terminal_id(&session_id).is_some() {
        return remote.close_terminal(&session_id).await;
    }
    terminal::close_terminal_session(&terminal_registry, &session_id)
}

// Declared here, not in `terminal/mod.rs`, because that file is at its size baseline.
#[cfg(test)]
#[path = "lifecycle_tests.rs"]
mod lifecycle_tests;
