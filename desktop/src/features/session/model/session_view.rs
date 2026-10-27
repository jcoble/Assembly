//! `SessionView` owns one open session's items and the newest turn, plan and context, from its rows. Live rows come
//! in seq order (the socket client checks the gaps); older pages go on top. Ported from rows/store.ts's `View`,
//! `applyRow`, `prependPage`, `activeRun` and `runStartedAt`.

use std::collections::HashMap;

use super::change::Change;
use super::item::{Item, ItemKind};
use crate::features::session::rows::plan::PlanItem;
use crate::features::session::rows::row::{Row, RowBody, TurnState};
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
    pub fn apply(&mut self, row: &Row) -> Change {
        self.newest = self.newest.max(row.seq);
        let key = key_of(row);
        let at = self.index.get(&key).copied();
        let run_id = row.run_id.clone();
        let kind = match RowBody::of(row) {
            RowBody::Turn { state, reason } => {
                if self.turn.as_ref().is_none_or(|turn| row.seq > turn.seq) {
                    self.turn = Some(Turn {
                        seq: row.seq,
                        run_id: run_id.clone(),
                        state,
                        at: row.created_at,
                    });
                }
                // A run the agent started itself has no prompt above it, so its reason stands in for one.
                match reason {
                    Some(text) if at.is_none() && !text.is_empty() => ItemKind::Note { text },
                    _ => return Change::Nothing,
                }
            }
            RowBody::Plan { items } => {
                if self.plan.as_ref().is_none_or(|(seq, _)| row.seq > *seq) {
                    self.plan = Some((row.seq, items));
                }
                return Change::Nothing;
            }
            RowBody::Context { used, size } => {
                if self.context.is_none_or(|(seq, ..)| row.seq > seq) {
                    self.context = Some((row.seq, used, size));
                }
                return Change::Nothing;
            }
            RowBody::Delta { from, blocks, .. } => {
                if let Some(i) = at
                    && let ItemKind::Message {
                        blocks: have,
                        done,
                        at,
                    } = &mut self.items[i].kind
                {
                    if !*done {
                        have.truncate(from);
                        have.extend(blocks);
                    }
                    *at = row.created_at;
                    return Change::Updated(i);
                }
                ItemKind::Message {
                    blocks,
                    done: false,
                    at: row.created_at,
                }
            }
            RowBody::Message { blocks } => {
                if let Some(i) = at
                    && let ItemKind::Message {
                        blocks: have,
                        done,
                        at,
                    } = &mut self.items[i].kind
                {
                    (*have, *done, *at) = (blocks, true, row.created_at);
                    return Change::Updated(i);
                }
                ItemKind::Message {
                    blocks,
                    done: true,
                    at: row.created_at,
                }
            }
            RowBody::Thinking { text, done } => {
                if let Some(i) = at
                    && let ItemKind::Thinking {
                        text: have,
                        done: was,
                    } = &mut self.items[i].kind
                {
                    (*have, *was) = (text, done);
                    return Change::Updated(i);
                }
                ItemKind::Thinking { text, done }
            }
            RowBody::Tool(tool) => {
                if let Some(i) = at
                    && let ItemKind::Tool { tool: have, .. } = &mut self.items[i].kind
                {
                    *have = tool;
                    return Change::Updated(i);
                }
                ItemKind::Tool {
                    item_id: row.item_id.clone().unwrap_or_else(|| key.clone()),
                    tool,
                }
            }
            RowBody::Ask(ask) => {
                if let Some(i) = at
                    && let ItemKind::Ask { ask: have, .. } = &mut self.items[i].kind
                {
                    *have = ask;
                    return Change::Updated(i);
                }
                ItemKind::Ask {
                    ask_id: id_after(row, "ask:"),
                    ask,
                }
            }
            RowBody::Task(task) => {
                if let Some(i) = at
                    && let ItemKind::Task { task: have, .. } = &mut self.items[i].kind
                {
                    *have = task;
                    return Change::Updated(i);
                }
                ItemKind::Task {
                    task_id: id_after(row, "task:"),
                    task,
                }
            }
            RowBody::Subagent(subagent) => {
                if let Some(i) = at
                    && let ItemKind::Subagent { subagent: have, .. } = &mut self.items[i].kind
                {
                    *have = subagent;
                    return Change::Updated(i);
                }
                ItemKind::Subagent {
                    acp_session_id: id_after(row, "subagent:"),
                    subagent,
                }
            }
            RowBody::Stats(stats) => {
                let run = stats.run_id.clone();
                self.stats.insert(run, stats.clone());
                ItemKind::Stats { stats }
            }
            RowBody::User { text, from } => ItemKind::User {
                text,
                at: row.created_at,
                from,
            },
            RowBody::Error { message, code } => ItemKind::Error { message, code },
            RowBody::Other { kind } => ItemKind::Other { kind },
        };
        if let Some(i) = at {
            // A row it couldn't read showed by name; a readable one for the same item draws it in its place.
            if matches!(self.items[i].kind, ItemKind::Other { .. })
                && !matches!(kind, ItemKind::Other { .. })
            {
                self.items[i].kind = kind;
                return Change::Updated(i);
            }
            return Change::Nothing;
        }
        self.index.insert(key.clone(), self.items.len());
        self.items.push(Item { key, run_id, kind });
        Change::Appended
    }

    /// Puts an older page above what's loaded, each item placed by its first seq. What's loaded is newer, so it
    /// wins: no loaded item, plan, turn, context or stats is replaced. Returns how many items went in at the top.
    pub fn prepend_page(&mut self, mut rows: Vec<Row>, has_before: bool) -> usize {
        rows.sort_by_key(first_seq);
        let mut page = SessionView::default();
        for row in &rows {
            page.apply(row);
        }
        let mut top: Vec<Item> = page
            .items
            .into_iter()
            .filter(|item| !self.index.contains_key(&item.key))
            .collect();
        let count = top.len();
        top.append(&mut self.items);
        self.items = top;
        self.index = self
            .items
            .iter()
            .enumerate()
            .map(|(i, item)| (item.key.clone(), i))
            .collect();
        for (run, stats) in page.stats {
            self.stats.entry(run).or_insert(stats);
        }
        self.plan = self.plan.take().or(page.plan);
        self.turn = self.turn.take().or(page.turn);
        self.context = self.context.or(page.context);
        self.newest = self.newest.max(page.newest);
        if let Some(lowest) = rows.iter().map(first_seq).min() {
            self.oldest = Some(self.oldest.map_or(lowest, |oldest| oldest.min(lowest)));
        }
        self.has_before = has_before;
        count
    }

    /// The run going now: the newest turn row's while it's started; else `known` (the page's activeRunId on open,
    /// or the run just sent) until a turn row or stats for it arrive.
    pub fn active_run(&self, known: Option<&str>) -> Option<String> {
        if let Some(turn) = &self.turn
            && turn.state == TurnState::Started
        {
            return turn.run_id.clone();
        }
        // A sent run counts until its first turn row; one with stats has ended, whatever run ended after it.
        let known = known?;
        let turn_run = self.turn.as_ref().and_then(|turn| turn.run_id.as_deref());
        (turn_run != Some(known) && !self.stats.contains_key(known)).then(|| known.to_string())
    }

    /// When the run going now started, from its started turn row; `None` when no such row is loaded.
    pub fn run_started_at(&self, run_id: &str) -> Option<i64> {
        let turn = self.turn.as_ref()?;
        (turn.run_id.as_deref() == Some(run_id) && turn.state == TurnState::Started)
            .then_some(turn.at)
    }
}

/// A row's item key: a row with no item is its own, by seq; deltas share their message's.
pub fn key_of(row: &Row) -> String {
    match &row.item_id {
        None => format!("seq:{}", row.seq),
        Some(item) => format!(
            "{}:{item}",
            if row.kind == "delta" {
                "message"
            } else {
                &row.kind
            }
        ),
    }
}

fn first_seq(row: &Row) -> i64 {
    row.first_seq.unwrap_or(row.seq)
}

/// The id an ask, task or subagent row's item carries after its prefix.
fn id_after(row: &Row, prefix: &str) -> String {
    let item = row.item_id.as_deref().unwrap_or_default();
    item.strip_prefix(prefix).unwrap_or(item).to_string()
}
