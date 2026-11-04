//! What a workflow step can be asked to do from the run view, and the words for what the engine says back. The engine
//! decides every one of these; this only leaves out what the run's and the step's state already rule out. Gates are
//! answered through their need, and revise has no button because the engine refuses it.
use crate::core::engine::{
    EngineRefusal,
    commands::{CommandReceipt, WindowCommand},
    workflow_items::{RunItem, StepItem},
};
use serde_json::{Value, json};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StepControl {
    Retry,
    Skip,
    Continue,
}

impl StepControl {
    pub fn label(self) -> &'static str {
        match self {
            Self::Retry => "Retry",
            Self::Skip => "Skip",
            Self::Continue => "Continue",
        }
    }

    fn wire(self, step_id: &str) -> Value {
        match self {
            Self::Retry => json!({"kind":"retry_workflow_step","stepId":step_id}),
            Self::Skip => json!({"kind":"decide_workflow_step","stepId":step_id,"decision":"skip"}),
            Self::Continue => {
                json!({"kind":"decide_workflow_step","stepId":step_id,"decision":"continue"})
            }
        }
    }
}

fn reason_is(step: &StepItem, reasons: &[&str]) -> bool {
    step.outcome_reason
        .as_deref()
        .is_some_and(|reason| reasons.contains(&reason))
}

/// A step that has used its attempts still offers Retry: the engine's refusal is what explains it.
pub fn offered(run: &RunItem, step: &StepItem) -> Vec<StepControl> {
    if matches!(
        run.state.as_str(),
        "succeeded" | "failed" | "cancelled" | "cancelling"
    ) {
        return Vec::new();
    }
    let stuck = [
        "session_lost",
        "timed_out",
        "interrupted",
        "command_uncertain",
    ];
    let gate = step.kind == "gate";
    let mut controls = Vec::new();
    if step.state == "failed" || (step.state == "waiting" && reason_is(step, &stuck)) {
        controls.push(StepControl::Retry);
    }
    let optional = !step.required || (step.rule_skippable && reason_is(step, &["condition_false"]));
    if !gate && step.state == "pending" && optional {
        controls.push(StepControl::Skip);
    }
    if !gate
        && step.state == "waiting"
        && reason_is(step, &["scope_required"])
        && step.scope_granted
        && step.needs_me_ids.is_empty()
    {
        controls.push(StepControl::Continue);
    }
    controls
}

pub fn command(
    run: &RunItem,
    step_id: &str,
    control: StepControl,
) -> Result<WindowCommand, EngineRefusal> {
    let epoch = run
        .owner_epoch
        .filter(|epoch| *epoch >= 0)
        .ok_or_else(|| EngineRefusal {
            code: "session_unavailable".into(),
            message: "The session that owns this workflow is not available.".into(),
        })?;
    Ok(WindowCommand {
        command_id: uuid::Uuid::new_v4().to_string(),
        entity_id: run.id.clone(),
        expected_version: run.version,
        epoch,
        machine: Some(run.machine.clone()),
        surface: Some("rail".into()),
        command: control.wire(step_id),
    })
}

fn words(code: Option<&str>) -> String {
    match code {
        Some("attempts_exhausted") => "This step has used all its attempts.".into(),
        Some("executor_quiescence_required") => {
            "The step's last attempt is still stopping. Try again once it has.".into()
        }
        Some("required_step") => "This step is required, so it can't be skipped.".into(),
        Some("invalid_transition") => "The workflow can't do that in its current state.".into(),
        Some("already_resolved") => "This has already been decided.".into(),
        Some("stale_epoch") => "The session that owns this workflow has changed.".into(),
        Some("forbidden") => "This window isn't allowed to do that.".into(),
        Some("wrong_machine") => "This workflow belongs to another machine.".into(),
        Some("version_conflict") => "The workflow changed on the way. Try again.".into(),
        Some(code) => format!("The workflow refused that ({code})."),
        None => "The workflow didn't take that.".into(),
    }
}

/// What to say under the step, when the answer wasn't that it was done.
pub fn refusal(answer: Result<CommandReceipt, EngineRefusal>) -> Option<String> {
    match answer {
        Ok(receipt) if receipt.applied => None,
        Ok(receipt) => Some(words(receipt.error.as_deref())),
        Err(refusal) => Some(refusal.message),
    }
}
