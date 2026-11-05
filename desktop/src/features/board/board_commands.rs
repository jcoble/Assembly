//! Board actions as the shared window command. The engine validates and applies them; a refused or unknown outcome is
//! never sent again and never moves the selection.
use serde_json::{Value, json};
use uuid::Uuid;

use crate::core::engine::EngineRefusal;
use crate::core::engine::commands::{CommandReceipt, WindowCommand};
use crate::core::engine::workflow_items::{RunItem, StepItem};

#[derive(Clone, Debug, PartialEq)]
pub enum BoardRunAction {
    Pause,
    Resume,
    Cancel,
    Rerun,
    RetryFailed,
    Keep,
    Undo { token: String },
    Grant { step_id: String, scope: String },
    RetryStep { step_id: String },
    SkipStep { step_id: String },
    ContinueStep { step_id: String },
}

/// The controls a step's row carries, from what the engine says it would accept. A word this build doesn't know draws nothing.
pub fn step_recovery(step: &StepItem) -> Vec<(&'static str, BoardRunAction)> {
    let step_id = || step.id.clone();
    step.recovery
        .iter()
        .filter_map(|word| match word.as_str() {
            "retry" => Some(("Retry", BoardRunAction::RetryStep { step_id: step_id() })),
            "skip" => Some(("Skip", BoardRunAction::SkipStep { step_id: step_id() })),
            "continue" => Some((
                "Continue",
                BoardRunAction::ContinueStep { step_id: step_id() },
            )),
            _ => None,
        })
        .collect()
}

impl BoardRunAction {
    /// Rerun and Retry start a linked new run, and the original is never reused.
    pub fn starts_a_new_run(&self) -> bool {
        matches!(self, Self::Rerun | Self::RetryFailed)
    }
}

fn refused(code: &str, message: &str) -> EngineRefusal {
    EngineRefusal {
        code: code.into(),
        message: message.into(),
    }
}

fn new_run_command(
    source: &RunItem,
    kind: &str,
    command_id: String,
    new_run_id: Option<String>,
    request_key: String,
) -> Result<WindowCommand, EngineRefusal> {
    let entity_id = new_run_id
        .filter(|id| Uuid::parse_str(id).is_ok())
        .ok_or_else(|| refused("bad_args", "A new run needs its own id."))?;
    if request_key.is_empty() {
        return Err(refused("bad_args", "A new run needs a request key."));
    }
    Ok(WindowCommand {
        command_id,
        entity_id,
        expected_version: 0,
        epoch: 0,
        machine: Some(source.machine.clone()),
        surface: None,
        command: json!({"kind": kind, "sourceRunId": source.id, "requestKey": request_key}),
    })
}

fn same_run_command(
    source: &RunItem,
    command: Value,
    command_id: String,
) -> Result<WindowCommand, EngineRefusal> {
    let epoch = source
        .owner_epoch
        .filter(|epoch| *epoch >= 0)
        .ok_or_else(|| {
            refused(
                "session_unavailable",
                "The run's owning session is not available.",
            )
        })?;
    Ok(WindowCommand {
        command_id,
        entity_id: source.id.clone(),
        expected_version: source.version,
        epoch,
        machine: Some(source.machine.clone()),
        surface: None,
        command,
    })
}

pub fn run_command(
    source: &RunItem,
    action: &BoardRunAction,
    command_id: String,
    new_run_id: Option<String>,
    request_key: String,
) -> Result<WindowCommand, EngineRefusal> {
    let command = match action {
        BoardRunAction::Rerun => {
            return new_run_command(
                source,
                "rerun_workflow",
                command_id,
                new_run_id,
                request_key,
            );
        }
        BoardRunAction::RetryFailed => {
            return new_run_command(source, "retry_failed", command_id, new_run_id, request_key);
        }
        BoardRunAction::Pause => json!({"kind": "pause_workflow"}),
        BoardRunAction::Resume => json!({"kind": "resume_workflow"}),
        BoardRunAction::Cancel => json!({"kind": "cancel_run"}),
        BoardRunAction::Keep => json!({"kind": "keep_succeeded"}),
        BoardRunAction::Undo { token } if token.is_empty() => {
            return Err(refused("bad_args", "This can no longer be undone."));
        }
        BoardRunAction::Undo { token } => json!({"kind": "undo_keep_succeeded", "token": token}),
        BoardRunAction::Grant { step_id, scope } => {
            json!({"kind": "grant_scope", "stepId": step_id, "scope": scope})
        }
        BoardRunAction::RetryStep { step_id } => {
            json!({"kind": "retry_workflow_step", "stepId": step_id})
        }
        BoardRunAction::SkipStep { step_id } => {
            json!({"kind": "decide_workflow_step", "stepId": step_id, "decision": "skip"})
        }
        BoardRunAction::ContinueStep { step_id } => {
            json!({"kind": "decide_workflow_step", "stepId": step_id, "decision": "continue"})
        }
    };
    same_run_command(source, command, command_id)
}

/// The run to select once its row arrives: only a new run the engine applied.
pub fn selected_after(
    action: &BoardRunAction,
    new_run_id: Option<&str>,
    outcome: &Result<CommandReceipt, EngineRefusal>,
) -> Option<String> {
    let applied = matches!(outcome, Ok(receipt) if receipt.applied);
    (applied && action.starts_a_new_run())
        .then(|| new_run_id.map(str::to_owned))
        .flatten()
}

/// An applied Keep's command id is the engine's own undo token. Nothing else can be undone.
pub fn undo_token(
    action: &BoardRunAction,
    command_id: &str,
    outcome: &Result<CommandReceipt, EngineRefusal>,
) -> Option<String> {
    let applied = matches!(outcome, Ok(receipt) if receipt.applied);
    (applied && *action == BoardRunAction::Keep).then(|| command_id.to_owned())
}

/// One plain line for what came back. `None` when it went through.
pub fn refusal_line(outcome: &Result<CommandReceipt, EngineRefusal>) -> Option<String> {
    let (code, message) = match outcome {
        Ok(receipt) if receipt.applied => return None,
        Ok(receipt) => (receipt.error.clone().unwrap_or_default(), None),
        Err(error) => (error.code.clone(), Some(error.message.clone())),
    };
    Some(match code.as_str() {
        "version_conflict" => "This changed a moment ago.".into(),
        "stale_epoch" => "This session moved to another machine; try again.".into(),
        "outcome_unknown" => "Not confirmed. Check the run before trying again.".into(),
        "already_resolved" => "Already answered elsewhere.".into(),
        "invalid_transition" => "The run can't do that now.".into(),
        "wrong_machine" | "unavailable" => "That machine isn't available.".into(),
        "attempts_exhausted" => "This step has used all its attempts.".into(),
        "executor_quiescence_required" => {
            "The step is still stopping. Try again in a moment.".into()
        }
        "required_step" => "This step is required, so it can't be skipped.".into(),
        "revision_target_undefined" => "There's no earlier step to send this back to.".into(),
        _ => message.unwrap_or_else(|| "The engine refused this.".into()),
    })
}

#[cfg(test)]
#[path = "board_commands_tests.rs"]
mod tests;
