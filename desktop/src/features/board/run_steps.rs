//! What a run card shows of its steps: the pill, which rows are visible, and each row's pills. All of it comes from the
//! engine's recorded display. A row changes in place, and only a recorded attempt or "Run complete" adds one.
use gpui_kit::SharedString;

use crate::activity::{StepMark, StepRight, StepView, Tone};
use crate::core::engine::workflow_items::{RunItem, StepItem};
use crate::core::ui::activity::step_row::step_view;

/// More upcoming steps than this fold under "N more after these".
const UPCOMING_SHOWN: usize = 5;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Pill {
    Working,
    Paused,
    NeedsYou,
    Complete,
    CompletedWithIssues,
    Failed,
    Cancelled,
}

impl Pill {
    pub fn label(self) -> &'static str {
        match self {
            Self::Working => "Working",
            Self::Paused => "Paused",
            Self::NeedsYou => "Needs you",
            Self::Complete => "Complete",
            Self::CompletedWithIssues => "Completed with issues",
            Self::Failed => "Failed",
            Self::Cancelled => "Cancelled",
        }
    }

    pub fn tone(self) -> Tone {
        match self {
            Self::Working => Tone::Info,
            Self::Paused | Self::Cancelled => Tone::Neutral,
            Self::NeedsYou | Self::CompletedWithIssues => Tone::Warning,
            Self::Complete => Tone::Success,
            Self::Failed => Tone::Danger,
        }
    }
}

pub fn pill(run: &RunItem, steps: &[&StepItem], open_need: bool) -> Pill {
    match run.state.as_str() {
        "succeeded" => Pill::Complete,
        "cancelled" => Pill::Cancelled,
        "failed" if steps.iter().any(|step| step.state == "succeeded") => Pill::CompletedWithIssues,
        "failed" => Pill::Failed,
        _ if open_need => Pill::NeedsYou,
        _ if run.paused => Pill::Paused,
        _ => Pill::Working,
    }
}

fn is_upcoming(step: &StepItem) -> bool {
    !matches!(
        step.state.as_str(),
        "running" | "waiting" | "succeeded" | "failed" | "cancelled" | "skipped"
    )
}

/// Every finished and active step, and the first few upcoming. `more` counts the rest.
pub fn visible_steps<'a>(steps: &[&'a StepItem]) -> (Vec<&'a StepItem>, usize) {
    let mut upcoming = 0;
    let mut more = 0;
    let visible = steps
        .iter()
        .copied()
        .filter(|step| {
            if !is_upcoming(step) {
                return true;
            }
            upcoming += 1;
            let shown = upcoming <= UPCOMING_SHOWN;
            more += usize::from(!shown);
            shown
        })
        .collect();
    (visible, more)
}

fn step_pills(step: &StepItem, steps: &[&StepItem], finished: bool) -> Vec<(SharedString, Tone)> {
    let mut pills = Vec::new();
    match step.state.as_str() {
        "failed" => {
            let retried = step.definition_key.is_some()
                && steps.iter().any(|later| {
                    later.definition_key == step.definition_key
                        && later.attempt > step.attempt
                        && later.state == "succeeded"
                });
            pills.push(if retried {
                ("Retried".into(), Tone::Neutral)
            } else {
                ("Failed".into(), Tone::Danger)
            });
        }
        "succeeded" if finished => pills.push(("Done".into(), Tone::Success)),
        _ => {}
    }
    if step.result.as_deref() == Some("check") {
        pills.push(("Check".into(), Tone::Info));
    }
    if step.closed {
        pills.push(("Closed".into(), Tone::Neutral));
    }
    pills
}

/// The rows to draw, in order, with "Run complete" after a run that succeeded.
pub fn card_steps(run: &RunItem, steps: &[&StepItem], now_ms: i64) -> Vec<StepView> {
    let finished = matches!(run.state.as_str(), "succeeded" | "failed" | "cancelled");
    let mut rows = steps
        .iter()
        .map(|step| {
            let mut row = step_view(step, now_ms);
            row.tool = (step.kind == "tool").then(|| step.name.clone().into());
            let mut pills = step_pills(step, steps, finished);
            pills.append(&mut row.pills);
            row.pills = pills;
            row
        })
        .collect::<Vec<_>>();
    if run.state == "succeeded" {
        rows.push(StepView {
            key: "run-complete".into(),
            mark: StepMark::Done,
            label: "Run complete".into(),
            tool: None,
            detail: None,
            explanation: None,
            pills: Vec::new(),
            right: StepRight::None,
            attempt: None,
            scope: None,
            closed: false,
            actions: Vec::new(),
        });
    }
    rows
}
