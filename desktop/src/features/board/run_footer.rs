//! A run card's summary line and the actions its state allows (spec 6.6).
use crate::activity::format_elapsed;
use crate::core::engine::workflow_items::{RunItem, StepItem};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RunAction {
    Pause,
    Resume,
    Stop,
    Rerun,
    Retry(usize),
    KeepSucceeded,
    Undo,
}

impl RunAction {
    pub fn label(self) -> String {
        match self {
            Self::Pause => "Pause".into(),
            Self::Resume => "Resume".into(),
            Self::Stop => "Stop".into(),
            Self::Rerun => "Rerun".into(),
            Self::Retry(count) => format!("Retry {count} failed"),
            Self::KeepSucceeded => "Keep what succeeded".into(),
            Self::Undo => "Undo".into(),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Footer {
    pub summary: String,
    pub actions: Vec<RunAction>,
}

fn settled(step: &StepItem) -> bool {
    matches!(step.state.as_str(), "succeeded" | "failed" | "cancelled" | "skipped")
}

fn ran(run: &RunItem, now_ms: i64) -> Option<String> {
    let start = run.started_ms?;
    Some(format_elapsed(run.ended_ms.unwrap_or(now_ms) - start).to_string())
}

fn with_time(words: String, run: &RunItem, now_ms: i64, joiner: &str) -> String {
    match ran(run, now_ms) {
        Some(time) => format!("{words} · {joiner}{time}"),
        None => words,
    }
}

/// How many failed steps a retry would run again: not closed, and with any scope they need granted.
pub fn retryable(steps: &[&StepItem]) -> usize {
    steps
        .iter()
        .filter(|step| step.state == "failed" && !step.closed && (step.required_scope.is_none() || step.scope_granted))
        .count()
}

fn summary(run: &RunItem, steps: &[&StepItem], tools_called: Option<i64>, now_ms: i64) -> String {
    let succeeded = steps.iter().filter(|step| step.state == "succeeded").count();
    match run.state.as_str() {
        "succeeded" => match tools_called {
            Some(tools) => format!("{tools} tool{} called", if tools == 1 { "" } else { "s" }),
            None => with_time(format!("{} steps", steps.len()), run, now_ms, "ran "),
        },
        "failed" | "cancelled" => {
            with_time(format!("{} settled, {succeeded} succeeded", steps.iter().filter(|step| settled(step)).count()), run, now_ms, "ran ")
        }
        _ => with_time(format!("Step {} of {}", (succeeded + 1).min(steps.len().max(1)), steps.len()), run, now_ms, ""),
    }
}

/// `can_undo` holds only while the engine's undo token from an applied Keep hasn't been refused or dropped.
pub fn footer(run: &RunItem, steps: &[&StepItem], tools_called: Option<i64>, open_need: bool, can_undo: bool, now_ms: i64) -> Footer {
    let unsuccessful_all_closed = steps.iter().filter(|step| matches!(step.state.as_str(), "failed" | "cancelled")).all(|step| step.closed);
    let actions = match run.state.as_str() {
        _ if can_undo => vec![RunAction::Undo],
        "succeeded" => vec![RunAction::Rerun],
        "failed" if unsuccessful_all_closed => Vec::new(),
        "failed" => match retryable(steps) {
            0 => vec![RunAction::KeepSucceeded],
            count => vec![RunAction::Retry(count), RunAction::KeepSucceeded],
        },
        "cancelled" => Vec::new(),
        _ if open_need => Vec::new(),
        _ if run.paused => vec![RunAction::Resume, RunAction::Stop],
        _ => vec![RunAction::Pause, RunAction::Stop],
    };
    Footer { summary: summary(run, steps, tools_called, now_ms), actions }
}
