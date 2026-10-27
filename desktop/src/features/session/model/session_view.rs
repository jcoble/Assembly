//! `SessionView` owns one open session's items and the newest turn, plan and context, from its rows. Live rows come
//! in seq order (the socket client checks the gaps); older pages go on top. Ported from rows/store.ts's `View`,
//! `applyRow`, `prependPage`, `activeRun` and `runStartedAt`.

use std::collections::HashMap;

use super::change::Change;
use super::item::Item;
use crate::features::session::rows::plan::PlanItem;
use crate::features::session::rows::row::{Row, TurnState};
use crate::features::session::rows::turn_stats::TurnStats;

#[derive(Default)]
pub struct SessionView {
    pub items: Vec<Item>,
    /// Each item's index, by key.
    index: HashMap<String, usize>,
    /// Each run's stats, for the line under its turn and its fold.
    pub stats: HashMap<String, TurnStats>,
    /// The newest plan row's seq and items.
    pub plan: Option<(i64, Vec<PlanItem>)>,
    pub turn: Option<Turn>,
    /// The newest context window report, for the composer's ring: (seq, used, size).
    pub context: Option<(i64, i64, i64)>,
    /// The lowest seq loaded (the next page's `before`), and whether older rows exist.
    pub oldest: Option<i64>,
    pub has_before: bool,
    /// The newest seq applied: the rows subscription's `after` on a reopen.
    pub newest: i64,
}

/// The newest turn row: its seq, run, state and when it was written.
#[derive(Clone, Debug, PartialEq)]
pub struct Turn {
    pub seq: i64,
    pub run_id: Option<String>,
    pub state: TurnState,
    pub at: i64,
}

impl SessionView {
    /// Applies a row newer than every row applied so far: it updates its item in place, or adds one at the end.
    pub fn apply(&mut self, _row: &Row) -> Change {
        todo!()
    }

    /// Puts an older page above what's loaded, each item placed by its first seq. What's loaded is newer, so it
    /// wins: no loaded item, plan, turn, context or stats is replaced. Returns how many items went in at the top.
    pub fn prepend_page(&mut self, _rows: Vec<Row>, _has_before: bool) -> usize {
        todo!()
    }

    /// The run going now: the newest turn row's while it's started; else `known` (the page's activeRunId on open,
    /// or the run just sent) until a turn row or stats for it arrive.
    pub fn active_run(&self, _known: Option<&str>) -> Option<String> {
        todo!()
    }

    /// When the run going now started, from its started turn row; `None` when no such row is loaded.
    pub fn run_started_at(&self, _run_id: &str) -> Option<i64> {
        todo!()
    }
}
