//! What an engine step means for display, for rail and workflow/board consumers. The shared step list draws it.
use crate::activity::{StepMark, StepRight, StepView, Tone, format_elapsed};
use crate::core::engine::workflow_items::StepItem;
use gpui_kit::SharedString;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StepTone {
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
    Waiting,
}
#[derive(Clone, Debug)]
pub struct StepRowView {
    pub label: String,
    pub state: String,
    pub tone: StepTone,
    pub running: bool,
    pub detail: Option<String>,
    pub explanation: Option<String>,
    pub progress: Option<f64>,
    pub attempt: i64,
    pub max_attempts: i64,
    pub required_scope: Option<String>,
    pub scope_granted: bool,
    pub closed: bool,
    pub elapsed_ms: Option<i64>,
}
pub fn view(step: &StepItem, now_ms: i64) -> StepRowView {
    let tone = match step.state.as_str() {
        "running" => StepTone::Running,
        "succeeded" => StepTone::Succeeded,
        "failed" => StepTone::Failed,
        "cancelled" => StepTone::Cancelled,
        "skipped" => StepTone::Skipped,
        "waiting" => StepTone::Waiting,
        _ => StepTone::Pending,
    };
    let complete = matches!(
        tone,
        StepTone::Succeeded | StepTone::Failed | StepTone::Cancelled | StepTone::Skipped
    );
    let elapsed_ms = step.started_ms.and_then(|start| {
        step.ended_ms
            .or_else(|| (tone == StepTone::Running).then_some(now_ms))
            .map(|end| (end - start).max(0))
    });
    StepRowView {
        label: if complete {
            step.label_past.clone()
        } else {
            step.label_present.clone()
        },
        state: step.state.clone(),
        tone,
        running: tone == StepTone::Running,
        detail: step.detail.clone(),
        explanation: step.explanation.clone(),
        progress: step.progress,
        attempt: step.attempt,
        max_attempts: step.max_attempts,
        required_scope: step.required_scope.clone(),
        scope_granted: step.scope_granted,
        closed: step.closed,
        elapsed_ms,
    }
}
/// The shared list's view of a step: its state and badges as pills, and everything else in its own place.
pub fn step_view(step: &StepItem, now_ms: i64) -> StepView {
    let row = view(step, now_ms);
    let mark = match row.tone {
        StepTone::Succeeded => StepMark::Done,
        StepTone::Running => StepMark::Running,
        StepTone::Failed => StepMark::Failed,
        StepTone::Waiting => StepMark::Waiting,
        StepTone::Pending | StepTone::Cancelled | StepTone::Skipped => StepMark::Upcoming,
    };
    let mut pills = Vec::new();
    match row.tone {
        StepTone::Cancelled => pills.push(("Cancelled".into(), Tone::Neutral)),
        StepTone::Skipped => pills.push(("Skipped".into(), Tone::Neutral)),
        _ => {}
    }
    pills.extend(step.badges.iter().map(|badge| {
        (
            SharedString::from(
                badge
                    .as_str()
                    .map(str::to_owned)
                    .unwrap_or_else(|| badge.to_string()),
            ),
            Tone::Neutral,
        )
    }));
    let right = match (row.progress, row.elapsed_ms) {
        (Some(progress), _) => StepRight::Percent(progress.clamp(0., 100.).round() as u8),
        (None, Some(ms)) => StepRight::Elapsed(format_elapsed(ms)),
        (None, None) => StepRight::None,
    };
    StepView {
        actions: Vec::new(),
        key: step.id.clone().into(),
        mark,
        label: row.label.into(),
        tool: None,
        detail: row.detail.map(Into::into),
        explanation: row.explanation.map(Into::into),
        pills,
        right,
        attempt: (row.attempt > 0).then_some((row.attempt, row.max_attempts)),
        scope: row
            .required_scope
            .map(|scope| (scope.into(), row.scope_granted)),
        closed: row.closed,
    }
}
