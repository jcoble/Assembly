//! `Fold` is a finished turn's work folded under one "Worked for" heading above its final reply. The prompt, the
//! final reply, errors and notes stay out, and so does anything still waiting on the user or still running. Ported
//! from session/fold.ts (turnFolds, workSummary, toolTitle).

use std::collections::{HashMap, HashSet};

use super::item::Item;
use crate::features::session::rows::turn_stats::TurnStats;

#[derive(Clone, Debug, PartialEq)]
pub struct Fold {
    pub run_id: String,
    /// The keys of the items the heading hides while closed.
    pub work: HashSet<String>,
    /// The final reply's key: its copy button and time go under it.
    pub reply: Option<String>,
    /// "Edited 2 files, ran 3 commands".
    pub summary: String,
    pub duration_ms: Option<i64>,
}

impl Fold {
    /// Each finished run's fold, by run id. A run is finished once its stats are in and it isn't the one running.
    pub fn all(_items: &[Item], _stats: &HashMap<String, TurnStats>, _active_run: Option<&str>) -> HashMap<String, Fold> {
        todo!()
    }

    /// What the hidden work did, by ACP tool kind, with the Assembly helper tools in words.
    pub fn summary(_work: &[&Item]) -> String {
        todo!()
    }

    /// A tool row's title: an Assembly helper tool's in words (`mcp__assembly__start_session` is "Started a
    /// helper"), any other's name.
    pub fn tool_title(_name: &str) -> String {
        todo!()
    }
}
