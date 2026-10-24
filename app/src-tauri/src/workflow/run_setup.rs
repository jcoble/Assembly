//! A YAML run's own worktree, set up once before its first step (TSK-1618).
use super::*;

impl WorkflowEngine {
    /// Gives a YAML run its own worktree the first time it dispatches. If that fails, the steps about to start fail
    /// with the error and the run ends failed, so the Workflows screen shows why; Retry on a step tries again.
    pub(super) fn set_up_run_worktree(&self, run: &mut WorkflowRunRecord) -> Result<(), WorkflowError> {
        if run.definition.nodes.iter().all(|node| node.step.is_null()) || run.input_snapshot.get("worktree").is_some() {
            return Ok(());
        }
        if let Err(error) = create_run_worktree(run) {
            refresh_ready_and_phase(run);
            for node in run.nodes.iter_mut().filter(|node| node.state == WorkflowNodeState::Ready) {
                node.state = WorkflowNodeState::Failed;
                node.failure = Some(WorkflowFailure { code: "setup".into(), message: error.to_string(), budget: None });
                node.finished_at_ms = Some(self.clock.now_ms());
            }
            refresh_ready_and_phase(run);
            self.append(run, "workflow.worktree.failed", &format!("setup:{}:{}", run.id, run.last_sequence), None)?;
            return Err(error);
        }
        run.worktrees_allocated = 1;
        self.append(run, "workflow.worktree.allocated", "worktree", None)
    }
}

/// Creates the run's branch and worktree from the task, and points the run at it.
fn create_run_worktree(run: &mut WorkflowRunRecord) -> Result<(), WorkflowError> {
    let task: TaskSnapshot = serde_json::from_value(run.input_snapshot["task"].clone()).map_err(invalid_yaml)?;
    let number = task.key.strip_prefix("TSK-").filter(|value| !value.is_empty() && value.bytes().all(|c| c.is_ascii_digit()))
        .ok_or_else(|| invalid_yaml("Task key must be TSK-<number>"))?;
    let slug = task.title.to_ascii_lowercase().chars().map(|c| if c.is_ascii_alphanumeric() { c } else { '-' }).collect::<String>();
    let branch = format!("tsk-{number}-{}-{}", slug.trim_matches('-'), &stable_hash(&run.id)[8..]);
    let root = run.input_snapshot["cwd"].as_str().ok_or_else(|| invalid_yaml("Project root is required"))?;
    let path = crate::project_folders::create_project_worktree_sync(Path::new(root), &branch).map_err(WorkflowError::Runtime)?;
    run.input_snapshot["worktree"] = json!({"path":path,"branch":branch,"status":"","removed":false});
    run.input_snapshot["cwd"] = json!(path);
    Ok(())
}
