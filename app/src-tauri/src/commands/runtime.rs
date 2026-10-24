use crate::{
    kill_playwright_session_sync, kill_playwright_sessions_sync, kill_process_sync,
    list_playwright_sessions_sync, list_runtime_contexts_sync, PlaywrightCleanupResult,
    PlaywrightSessionInfo, ProcessKillResult, RuntimeContext, RuntimeContextProject,
};

#[tauri::command]
pub(crate) async fn list_runtime_contexts(
    projects: Vec<RuntimeContextProject>,
) -> Result<Vec<RuntimeContext>, String> {
    tauri::async_runtime::spawn_blocking(move || list_runtime_contexts_sync(projects))
        .await
        .map_err(|error| format!("Runtime context task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn list_playwright_sessions() -> Result<Vec<PlaywrightSessionInfo>, String> {
    tauri::async_runtime::spawn_blocking(list_playwright_sessions_sync)
        .await
        .map_err(|error| format!("Playwright session scan task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn kill_playwright_session(pgid: i32) -> Result<PlaywrightCleanupResult, String> {
    tauri::async_runtime::spawn_blocking(move || kill_playwright_session_sync(pgid))
        .await
        .map_err(|error| format!("Playwright session cleanup task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn kill_playwright_sessions() -> Result<PlaywrightCleanupResult, String> {
    tauri::async_runtime::spawn_blocking(kill_playwright_sessions_sync)
        .await
        .map_err(|error| format!("Playwright cleanup task failed: {error}"))?
}

/// Ask one process to stop with the polite stop signal only.
#[tauri::command]
pub(crate) async fn kill_process(
    pid: u32,
    expected_command: Option<String>,
) -> Result<ProcessKillResult, String> {
    tauri::async_runtime::spawn_blocking(move || kill_process_sync(pid, expected_command))
        .await
        .map_err(|error| format!("Stop process task failed: {error}"))
}
