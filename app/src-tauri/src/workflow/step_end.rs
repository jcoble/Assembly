//! What happens when a step ends (TSK-1601): its agent stops, and a step that must not change code is checked.
use super::*;

pub(super) const CHANGED_CODE: &str = "The step changed HEAD or the working tree; only the build step may change code";

impl WorkflowEngine {
    /// Stops an agent step's session and refuses its token once the step's end is recorded. A failure here is only
    /// logged: the result is already stored. An idle session is suspended, so a redo resumes it with a new token.
    pub(super) async fn end_agent_turn(&self, run: &WorkflowRunRecord, node_id: &str, owned_id: &str) {
        if run.definition.nodes.iter().any(|node| node.id == node_id && (node.step.get("run").is_some() || node.step.get("gate").is_some())) { return; }
        match crate::agent_conversation::messaging::endpoint() {
            Ok(endpoint) => endpoint.revoke(owned_id),
            Err(error) => eprintln!("workflow session {owned_id} keeps its token: {error}"),
        }
        if let Err(error) = self.runtime.cancel(owned_id).await {
            eprintln!("workflow session {owned_id} could not be stopped: {error}");
        }
    }
}

/// Refuses a definition in which two `agent:` steps can run at the same time; review steps may.
pub(super) fn refuse_parallel_agents(steps: &serde_json::Map<String, Value>) -> Result<(), WorkflowError> {
    let agents: Vec<_> = steps.iter().filter(|(_, step)| step.get("agent").is_some()).map(|(id, _)| id.as_str()).collect();
    for (index, a) in agents.iter().enumerate() {
        if let Some(b) = agents[index + 1..].iter().find(|b| !needs_ancestry(steps, a).contains(**b) && !needs_ancestry(steps, b).contains(*a)) {
            return Err(invalid_yaml(format!("steps {a} and {b} can run at the same time; agent steps must be ordered")));
        }
    }
    Ok(())
}

/// Review and `run` steps must leave the code as they found them. Agent steps join with `result:` (slice 2).
fn guards_code(run: &WorkflowRunRecord, node_id: &str) -> bool {
    run.definition.nodes.iter().any(|node| node.id == node_id && (node.step.get("review").is_some() || node.step.get("run").is_some()))
}

/// A fingerprint of HEAD, the status, the tracked changes and every untracked file's contents in the run's checkout,
/// so a rewrite of a file that was already dirty is seen too. An error is part of the fingerprint.
fn tree_state(run: &WorkflowRunRecord) -> String {
    let mut command = std::process::Command::new("sh");
    command.args(["-c", "{ git rev-parse HEAD && git status --porcelain && git diff HEAD --binary \
        && git ls-files --others --exclude-standard -z | xargs -0 -r git hash-object --; } 2>&1 | git hash-object --stdin"])
        .current_dir(run.input_snapshot["cwd"].as_str().unwrap_or_default());
    crate::bounded_process::output(&mut command, "Changed code", crate::bounded_process::LOCAL_COMMAND_TIMEOUT)
        .map_or_else(|error| error.to_string(), |output| String::from_utf8_lossy(&output.stdout).trim().to_string())
}

/// Records the checkout as a guarded step starts, so its end can be compared with it.
pub(super) fn record_tree(run: &mut WorkflowRunRecord, node_id: &str) {
    if guards_code(run, node_id) { run.input_snapshot["trees"][node_id] = json!(tree_state(run)); }
}

/// A guarded step that moved HEAD or changed the working tree since it started.
pub(super) fn changed_code(run: &WorkflowRunRecord, node_id: &str) -> bool {
    guards_code(run, node_id) && run.input_snapshot["trees"][node_id].as_str().is_some_and(|start| start != tree_state(run))
}
