//! `SessionView` owns one open session's items and the newest turn, plan and context, from its rows. Live rows come
//! in seq order (the socket client checks the gaps); older pages go on top. Ported from rows/store.ts's `View`,
//! `applyRow`, `prependPage`, `activeRun` and `runStartedAt`.

use std::collections::{HashMap, HashSet};

use super::change::Change;
use super::item::{Item, ItemKind};
use crate::features::session::rows::plan::PlanItem;
use crate::features::session::rows::row::{Row, RowBody, TurnState};
use crate::features::session::rows::turn_stats::TurnStats;

/// The persisted owner row behind a subagent card, never a provider-native ID synthesized as a row.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RowReference {
    pub session_id: String,
    pub item_id: Option<String>,
    pub seq: i64,
}

/// One context window report as the agent sent it; a count it didn't send stays `None`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ContextReport {
    pub seq: i64,
    pub used: i64,
    pub size: i64,
    pub input_tokens: Option<i64>,
    pub output_tokens: Option<i64>,
}

#[derive(Default)]
pub struct SessionView {
    pub items: Vec<Item>,
    /// Each item's index, by key.
    index: HashMap<String, usize>,
    row_refs: HashMap<String, RowReference>,
    /// Each run's stats, for the line under its turn and its fold.
    pub stats: HashMap<String, TurnStats>,
    /// The newest plan row's seq and items.
    pub plan: Option<(i64, Vec<PlanItem>)>,
    pub turn: Option<Turn>,
    /// The newest context window report, for the composer's ring.
    pub context: Option<ContextReport>,
    /// The lowest seq loaded (the next page's `before`), and whether older rows exist.
    pub oldest: Option<i64>,
    pub has_before: bool,
    /// The newest seq applied: the rows subscription's `after` on a reopen.
    pub newest: i64,
    pub pending: HashMap<String, String>,
    /// Owner clients with positive provider acknowledgment, never merely a reserved user row.
    pub user_clients: HashSet<String>,
    user_items: HashMap<String, String>,
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
    pub fn row_reference(&self, item_key: &str) -> Option<&RowReference> {
        self.row_refs.get(item_key)
    }

    pub fn apply(&mut self, row: &Row) -> Change {
        if row.kind == "subagent" {
            let reference = RowReference {
                session_id: row.session_id.clone(),
                item_id: row.item_id.clone(),
                seq: row.seq,
            };
            let saved = self
                .row_refs
                .entry(key_of(row))
                .or_insert_with(|| reference.clone());
            if saved.seq < reference.seq {
                *saved = reference;
            }
        }
        if row.kind == "user"
            && row.payload["from"].is_null()
            && let Some(client) = row.payload["clientId"].as_str()
        {
            let key = key_of(row);
            self.user_items.insert(client.to_string(), key.clone());
            if positive_acknowledgment(&row.payload["acknowledgment"]) {
                self.user_clients.insert(client.to_string());
            }
            self.reconcile_pending_row(client, &key);
        }
        self.newest = self.newest.max(row.seq);
        let key = key_of(row);
        let at = self.index.get(&key).copied();
        if let Some(i) = at {
            self.items[i].at = row.created_at;
        }
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
            RowBody::Context {
                used,
                size,
                input_tokens,
                output_tokens,
            } => {
                if self.context.is_none_or(|context| row.seq > context.seq) {
                    self.context = Some(ContextReport {
                        seq: row.seq,
                        used,
                        size,
                        input_tokens,
                        output_tokens,
                    });
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
            RowBody::User {
                text,
                from,
                from_machine,
                sender_label,
            } => ItemKind::User {
                text,
                at: row.created_at,
                from,
                from_machine,
                sender_label,
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
        self.items.push(Item {
            key,
            run_id,
            kind,
            at: row.created_at,
        });
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
        for (key, reference) in page.row_refs {
            let saved = self
                .row_refs
                .entry(key)
                .or_insert_with(|| reference.clone());
            if saved.seq < reference.seq {
                *saved = reference;
            }
        }
        self.user_clients.extend(page.user_clients.iter().cloned());
        for (client, key) in &page.user_items {
            self.user_items
                .entry(client.clone())
                .or_insert_with(|| key.clone());
            let selected = self.user_items[client].clone();
            self.reconcile_pending_row(client, &selected);
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

    fn reindex(&mut self) {
        self.index = self
            .items
            .iter()
            .enumerate()
            .map(|(i, item)| (item.key.clone(), i))
            .collect();
    }

    pub fn retain_pending(&mut self, clients: &HashSet<String>) {
        let removed: HashSet<String> = self
            .pending
            .keys()
            .filter(|key| {
                !self
                    .pending_client(key)
                    .is_some_and(|client| clients.contains(client))
            })
            .cloned()
            .collect();
        if removed.is_empty() {
            return;
        }
        self.pending.retain(|key, _| !removed.contains(key));
        self.items
            .retain(|item| !item.key.starts_with("pending:") || !removed.contains(&item.key));
        self.reindex();
    }

    pub fn pending_client<'a>(&'a self, key: &'a str) -> Option<&'a str> {
        key.strip_prefix("pending:").or_else(|| {
            self.user_items
                .iter()
                .find_map(|(client, item)| (item == key).then_some(client.as_str()))
        })
    }

    /// A pre-provider row replaces the optimistic text but keeps its Engine delivery status.
    fn reconcile_pending_row(&mut self, client: &str, key: &str) {
        let optimistic = format!("pending:{client}");
        if let Some(state) = self.pending.remove(&optimistic) {
            self.pending.insert(key.to_string(), state);
        }
        if self.index.contains_key(&optimistic) {
            self.items.retain(|item| item.key != optimistic);
            self.reindex();
        }
        if self.user_clients.contains(client) {
            self.pending.remove(key);
        }
    }

    /// Local presentation only: no seq, provider row or run is invented.
    pub fn pending_prompt(&mut self, client: &str, text: &str, state: &str, at: i64) {
        if self.user_clients.contains(client) {
            return;
        }
        let key = self
            .user_items
            .get(client)
            .cloned()
            .unwrap_or_else(|| format!("pending:{client}"));
        self.pending.insert(key.clone(), state.to_string());
        if self.index.contains_key(&key) {
            return;
        }
        self.index.insert(key.clone(), self.items.len());
        self.items.push(Item {
            key,
            run_id: None,
            at,
            kind: ItemKind::User {
                text: text.to_string(),
                at,
                from: None,
                from_machine: None,
                sender_label: None,
            },
        });
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

    /// The plan's items, when the turn going now wrote it: an earlier turn's plan is no step of this one.
    pub fn turn_plan(&self) -> Option<&[PlanItem]> {
        let turn = self
            .turn
            .as_ref()
            .filter(|turn| turn.state == TurnState::Started)?;
        let (_, items) =
            (self.plan.as_ref()).filter(|(seq, items)| *seq > turn.seq && !items.is_empty())?;
        Some(items)
    }

    /// When the run going now started, from its started turn row; `None` when no such row is loaded.
    pub fn run_started_at(&self, run_id: &str) -> Option<i64> {
        let turn = self.turn.as_ref()?;
        (turn.run_id.as_deref() == Some(run_id) && turn.state == TurnState::Started)
            .then_some(turn.at)
    }
}

/// Same positive evidence shapes as the owning RowStore; absent/malformed evidence stays unknown.
fn positive_acknowledgment(ack: &serde_json::Value) -> bool {
    match ack["kind"].as_str() {
        Some("prompt_inserted") => ack["providerMessageId"]
            .as_str()
            .is_some_and(|id| !id.trim().is_empty()),
        Some("steer_injected") => ack["providerMessageId"].is_null(),
        _ => false,
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
