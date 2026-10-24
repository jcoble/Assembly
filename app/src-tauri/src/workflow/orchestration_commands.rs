use crate::agent_conversation::manager::AgentRuntimeManager;
use crate::workflow::orchestration::{
    list_orchestration_runs_sync, record_orchestration_event_sync, OrchestrationEvent,
    OrchestrationRun,
};
use crate::RuntimeContextProject;

#[tauri::command]
pub(crate) async fn list_orchestration_runs(
    manager: tauri::State<'_, AgentRuntimeManager>,
    projects: Vec<RuntimeContextProject>,
) -> Result<Vec<OrchestrationRun>, String> {
    let store = manager.store_handle();
    tauri::async_runtime::spawn_blocking(move || list_orchestration_runs_sync(&store, projects))
        .await
        .map_err(|error| format!("Orchestration run task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn record_orchestration_event(
    manager: tauri::State<'_, AgentRuntimeManager>,
    event: OrchestrationEvent,
) -> Result<OrchestrationRun, String> {
    let store = manager.store_handle();
    tauri::async_runtime::spawn_blocking(move || record_orchestration_event_sync(&store, event))
        .await
        .map_err(|error| format!("Orchestration event task failed: {error}"))?
}
