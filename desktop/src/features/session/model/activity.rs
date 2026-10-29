//! A running turn's activity card: its tool calls as steps, each with how long it ran, and the footer under them
//! ("3 steps · 0:41", or "Step 3 of 5 · 0:41" when the session has a plan). A page carries only an item's newest row, so a step counts from when the item before it in the
//! turn was last written (or the turn started) to its own newest row, or to now while it runs.

use super::item::{Item, ItemKind};
use crate::features::session::rows::tool::ToolState;

/// One step: its item's index and how long it ran, in ms.
#[derive(Clone, Debug, PartialEq)]
pub struct Step {
    pub at: usize,
    pub took_ms: i64,
}

/// The tool calls of `run`, which started at `started_at`, as steps; `now` is the backend clock.
pub fn steps(items: &[Item], run: &str, started_at: i64, now: i64) -> Vec<Step> {
    let mut since = started_at;
    let mut steps = Vec::new();
    for (at, item) in items.iter().enumerate() {
        if item.run_id.as_deref() != Some(run) {
            continue;
        }
        if let ItemKind::Tool { tool, .. } = &item.kind {
            // A running call's newest row is usually its start, which a call that ran beside it and finished first
            // doesn't move.
            let (start, end) = if tool.state == ToolState::Started {
                (since.min(item.at), now)
            } else {
                (since, item.at)
            };
            steps.push(Step {
                at,
                took_ms: (end - start).max(0),
            });
        }
        since = since.max(item.at);
    }
    steps
}

/// "3 steps · 0:41", "1 step · 1:02:05", or only the time before the first step; with a plan, its step and length
/// ("Step 3 of 5 · 0:41") in place of the count.
pub fn footer(steps: usize, plan: Option<(usize, usize)>, ms: i64) -> String {
    let seconds = ms.max(0) / 1000;
    let (hours, minutes, seconds) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
    let time = if hours > 0 {
        format!("{hours}:{minutes:02}:{seconds:02}")
    } else {
        format!("{minutes}:{seconds:02}")
    };
    match (plan, steps) {
        (Some((step, of)), _) => format!("Step {step} of {of} · {time}"),
        (None, 0) => time,
        (None, 1) => format!("1 step · {time}"),
        (None, _) => format!("{steps} steps · {time}"),
    }
}
