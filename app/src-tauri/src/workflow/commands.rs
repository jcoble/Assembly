use tauri::{Emitter, Manager};

use crate::workflow::{TaskSnapshot, WorkflowEngine, WorkflowRunRecord};

#[tauri::command]
pub(crate) async fn list_workflow_runs(
    engine: tauri::State<'_, WorkflowEngine>,
) -> Result<Vec<WorkflowRunRecord>, String> {
    engine.list_runs().map_err(|error| error.to_string())
}

pub(crate) fn emit_workflow_run_updated(app: &tauri::AppHandle, run: &WorkflowRunRecord) {
    let _ = app.emit("workflow-run-updated", run);
}

/// What the Workflows screen watches: the board (every run, as a summary) and the run it has open
/// (in full). Nothing is sent while it is closed.
#[derive(Default)]
pub(crate) struct WorkflowWatch(std::sync::Mutex<(bool, Option<String>)>);

#[tauri::command]
pub(crate) fn watch_workflow_runs(watch: tauri::State<'_, WorkflowWatch>, board: bool, run_id: Option<String>) {
    *watch.0.lock().unwrap() = (board, run_id);
}

/// The open run goes in full; any other run goes to the board without step outputs or artifacts.
fn run_for_watchers(watch: &(bool, Option<String>), mut run: WorkflowRunRecord) -> Option<WorkflowRunRecord> {
    if watch.1.as_deref() == Some(run.id.as_str()) { return Some(run); }
    if !watch.0 { return None; }
    for node in &mut run.nodes {
        node.structured_output = None;
        node.artifacts.clear();
    }
    Some(run)
}

pub(crate) fn send_to_watchers(app: &tauri::AppHandle, run: WorkflowRunRecord) {
    let watch = app.state::<WorkflowWatch>().0.lock().unwrap().clone();
    if let Some(run) = run_for_watchers(&watch, run) { let _ = app.emit("workflow-run-changed", run); }
}

/// Every stored run change (deadlines, `run` steps, review resets and MCP answers included) reaches
/// the screen, 250 ms after the first change of a burst and once per changed run.
pub(crate) async fn forward_run_changes(app: tauri::AppHandle, engine: WorkflowEngine) {
    use tokio::sync::broadcast::error::{RecvError, TryRecvError};
    let mut changes = engine.run_changes();
    loop {
        let mut ids = std::collections::BTreeSet::new();
        let mut lagged = false;
        match changes.recv().await {
            Ok((id, _)) => { ids.insert(id); }
            Err(RecvError::Lagged(_)) => lagged = true,
            Err(RecvError::Closed) => break,
        }
        tokio::time::sleep(std::time::Duration::from_millis(250)).await;
        loop {
            match changes.try_recv() {
                Ok((id, _)) => { ids.insert(id); }
                Err(TryRecvError::Lagged(_)) => lagged = true,
                Err(_) => break,
            }
        }
        if *app.state::<WorkflowWatch>().0.lock().unwrap() == (false, None) { continue; }
        let runs = if lagged { engine.list_runs().unwrap_or_default() }
            else { ids.iter().filter_map(|id| engine.get_run(id).ok()).collect() };
        for run in runs { send_to_watchers(&app, run); }
    }
}

#[tauri::command]
pub(crate) async fn create_workflow_run(
    app: tauri::AppHandle,
    engine: tauri::State<'_, WorkflowEngine>,
    repo_root: String,
    workflow_path: String,
    task: TaskSnapshot,
    input: serde_json::Value,
    idempotency_key: String,
) -> Result<WorkflowRunRecord, String> {
    let run = engine
        .create_run_from_file(std::path::Path::new(&repo_root), std::path::Path::new(&workflow_path), task, input, idempotency_key)
        .map_err(|error| error.to_string())?;
    emit_workflow_run_updated(&app, &run);
    Ok(run)
}

#[tauri::command]
pub(crate) async fn get_workflow_run(
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
) -> Result<WorkflowRunRecord, String> {
    engine.get_run(&run_id).map_err(|error| error.to_string())
}

/// Every ledger row of one run on this Mac, for the run timeline.
#[tauri::command]
pub(crate) async fn workflow_history(
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
) -> Result<serde_json::Value, String> {
    crate::workflow::execute_operation(&engine, "history", serde_json::json!({ "runId": run_id })).await
}

/// Every review report of one run on this Mac, for the review close-up.
#[tauri::command]
pub(crate) async fn workflow_review_reports(
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
) -> Result<serde_json::Value, String> {
    crate::workflow::execute_operation(&engine, "reviews", serde_json::json!({ "runId": run_id })).await
}

/// Workflow steps waiting on the owner on this Mac and every connected machine, each tagged
/// with its machine. A machine that cannot be reached is left out.
#[tauri::command]
pub(crate) async fn list_owner_inbox(
    engine: tauri::State<'_, WorkflowEngine>,
    remote: tauri::State<'_, crate::agent_conversation::remote::RemoteConnectionManager>,
    request_id: u64,
) -> Result<Vec<serde_json::Value>, String> {
    let work = async {
        let mut steps = Vec::new();
        for machine in remote.message_machines("") {
            let page = if machine == crate::agent_conversation::remote::LOCAL_MACHINE {
                crate::workflow::execute_operation(&engine, "inbox", serde_json::Value::Null).await
            } else {
                remote.workflow_operation(&machine, "inbox".into(), serde_json::Value::Null, Some(request_id)).await
            };
            let Ok(serde_json::Value::Array(page)) = page else { continue };
            steps.extend(page.into_iter().map(|mut step| { step["machine"] = serde_json::json!(machine); step }));
        }
        steps
    };
    Ok(remote.unless_cancelled(request_id, work).await.unwrap_or_default())
}

/// The repository's workflow files, as paths relative to `repo_root`.
#[tauri::command]
pub(crate) async fn list_workflow_files(repo_root: String) -> Result<Vec<String>, String> {
    Ok(crate::workflow::workflow_files(std::path::Path::new(&repo_root)))
}

#[tauri::command]
pub(crate) async fn answer_workflow_pause(
    app: tauri::AppHandle,
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
    node_id: String,
    decision: String,
    reply: Option<String>,
    idempotency_key: String,
) -> Result<WorkflowRunRecord, String> {
    let run = engine
        .answer_pause(&run_id, &node_id, &decision, reply, "owner", None, &idempotency_key, None)
        .await
        .map_err(|error| error.to_string())?;
    emit_workflow_run_updated(&app, &run);
    Ok(run)
}

#[tauri::command]
pub(crate) async fn start_workflow_run(
    app: tauri::AppHandle,
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
    idempotency_key: String,
) -> Result<WorkflowRunRecord, String> {
    let run = engine
        .start(&run_id, &idempotency_key)
        .await
        .map_err(|error| error.to_string())?;
    emit_workflow_run_updated(&app, &run);
    Ok(run)
}

#[tauri::command]
pub(crate) async fn pause_workflow_run(
    app: tauri::AppHandle,
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
    idempotency_key: String,
) -> Result<WorkflowRunRecord, String> {
    let run = engine
        .pause(&run_id, &idempotency_key)
        .map_err(|error| error.to_string())?;
    emit_workflow_run_updated(&app, &run);
    Ok(run)
}

#[tauri::command]
pub(crate) async fn resume_workflow_run(
    app: tauri::AppHandle,
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
    idempotency_key: String,
) -> Result<WorkflowRunRecord, String> {
    let run = engine
        .resume(&run_id, &idempotency_key)
        .await
        .map_err(|error| error.to_string())?;
    emit_workflow_run_updated(&app, &run);
    Ok(run)
}

#[tauri::command]
pub(crate) async fn cancel_workflow_run(
    app: tauri::AppHandle,
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
    idempotency_key: String,
) -> Result<WorkflowRunRecord, String> {
    let run = engine
        .cancel(&run_id, &idempotency_key)
        .await
        .map_err(|error| error.to_string())?;
    emit_workflow_run_updated(&app, &run);
    Ok(run)
}

#[tauri::command]
pub(crate) async fn retry_workflow_node(
    app: tauri::AppHandle,
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
    node_id: String,
    idempotency_key: String,
) -> Result<WorkflowRunRecord, String> {
    let run = engine
        .retry_node(&run_id, &node_id, &idempotency_key)
        .await
        .map_err(|error| error.to_string())?;
    emit_workflow_run_updated(&app, &run);
    Ok(run)
}

#[tauri::command]
pub(crate) async fn approve_workflow_gate(
    app: tauri::AppHandle,
    engine: tauri::State<'_, WorkflowEngine>,
    run_id: String,
    node_id: String,
    approval: serde_json::Value,
    idempotency_key: String,
) -> Result<WorkflowRunRecord, String> {
    let run = engine
        .approve_gate(&run_id, &node_id, approval, "owner", &idempotency_key, None)
        .await
        .map_err(|error| error.to_string())?;
    emit_workflow_run_updated(&app, &run);
    Ok(run)
}

#[cfg(test)]
mod tests {
    use super::run_for_watchers;
    use crate::workflow::WorkflowRunRecord;

    fn run(id: &str) -> WorkflowRunRecord {
        serde_json::from_value(serde_json::json!({
            "id": id, "workflowId": "w", "definition": {"id": "w", "name": "W"}, "state": "running",
            "phase": "implementing", "inputSnapshot": {}, "inputHash": "h", "createdAtMs": 1, "updatedAtMs": 2,
            "nodes": [{"id": "n1", "nodeId": "build", "roleId": "build", "state": "completed", "attempt": 1, "depth": 0,
                "ownedId": "o", "provider": "claude", "providerInstanceId": null, "startedAtMs": 1, "finishedAtMs": 2,
                "outputContract": "ImplementationReceipt", "structuredOutput": {"summary": "long"},
                "artifacts": [{"id": "a", "kind": "file", "path": "p", "url": null, "digest": null}],
                "gate": null, "leaseId": null, "failure": null}],
            "tokensUsed": 0, "toolTerminalsUsed": 0, "worktreesAllocated": 0, "lastSequence": 3
        })).unwrap()
    }

    #[test]
    fn workflow_watchers_get_the_open_run_in_full_other_runs_as_a_summary_and_nothing_when_closed() {
        assert_eq!(run_for_watchers(&(false, None), run("a")), None);
        assert_eq!(run_for_watchers(&(false, Some("b".into())), run("a")), None);
        assert_eq!(run_for_watchers(&(true, Some("a".into())), run("a")), Some(run("a")));
        let summary = run_for_watchers(&(true, Some("b".into())), run("a")).unwrap();
        assert_eq!((summary.nodes[0].structured_output.clone(), summary.nodes[0].artifacts.len()), (None, 0));
        assert_eq!(summary.nodes[0].state, run("a").nodes[0].state);
    }
}
