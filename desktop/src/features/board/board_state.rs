//! `BoardState` is the Board's logic apart from its drawing: which page reads to send and which answers to keep, the
//! expanded card's detail, and each question's answer on its way. The backend's SQL filters, counts, sorts and pages;
//! this only keeps what it sent back. Each read from the first page starts an epoch, and a page from an older one is
//! dropped. A change reads from the first page again, as many cards as are held, and swaps them in once that read is
//! done, so the reader keeps their place and the order is always fresh from SQL.

use gpui_kit::{ListOffset, px};
use std::collections::HashMap;

use crate::core::socket::update::Update;

use super::board_payload::{
    BoardAnswerArgs, BoardAnswerValue, BoardChange, BoardCounts, BoardPageArgs, BoardPageResult,
    BoardQuestion, BoardSummary, BoardTaskArgs, BoardTaskResult,
};

/// The states a card ends in: its duration is frozen and needs no clock.
pub const TERMINAL: [&str; 3] = ["completed", "failed", "cancelled"];

/// A question's identity: its machine, task and question key.
pub type QuestionId = (String, String, String);

/// A page read to send, and the epoch its answer must match.
pub struct PageRead {
    pub epoch: u64,
    pub args: BoardPageArgs,
}

/// A question's answer on its way, or the one that failed, with the key a second send of the same answer to the same
/// generation reuses.
#[derive(Default)]
pub struct Draft {
    pub value: Option<BoardAnswerValue>,
    pub generation: i32,
    pub answer_key: String,
    pub sending: bool,
    pub error: Option<String>,
}

#[derive(Default)]
pub struct BoardState {
    /// The task state shown, or `None` for all.
    pub filter: Option<String>,
    pub items: Vec<BoardSummary>,
    pub counts: Option<BoardCounts>,
    pub next_cursor: Option<String>,
    epoch: u64,
    /// The read on its way: the cards it has so far, and how many it needs before it replaces the list.
    reading: Option<(Vec<BoardSummary>, usize)>,
    /// A change came while a read was on its way, so one more read follows it.
    again: bool,
    /// The expanded card's machine and task, and its detail once read.
    pub selected: Option<(String, String)>,
    pub detail: Option<BoardTaskResult>,
    pub drafts: HashMap<QuestionId, Draft>,
    offline_machines: Vec<String>,
}

pub fn question_id(q: &BoardQuestion) -> QuestionId {
    (
        q.machine_id.clone(),
        q.task_key.clone(),
        q.question_key.clone(),
    )
}

impl BoardState {
    pub fn scroll_anchor(&self, top: ListOffset) -> Option<(String, String)> {
        if top.item_ix == 0 && top.offset_in_item == px(0.) {
            return None;
        }
        self.items
            .get(top.item_ix)
            .map(|item| (item.task.machine_id.clone(), item.task.task_key.clone()))
    }

    /// A remote source came back without reconnecting the local socket: reload its expanded detail.
    pub fn machines_changed(&mut self, offline: Vec<String>) -> Option<BoardTaskArgs> {
        let reconnected = self.selected.as_ref().is_some_and(|(machine, _)| {
            self.offline_machines.contains(machine) && !offline.contains(machine)
        });
        self.offline_machines = offline;
        reconnected.then(|| self.detail_read(None, None)).flatten()
    }

    /// Shows `filter`'s cards from their first page; a read on its way for another filter is stale.
    pub fn set_filter(&mut self, filter: Option<String>) -> PageRead {
        self.filter = filter;
        (self.items, self.next_cursor, self.again) = (Vec::new(), None, false);
        self.start(Vec::new(), 0, None)
    }

    /// Something changed: reads from the first page again until it holds as many cards as are shown. While a read is
    /// on its way, one more follows it instead.
    pub fn changed(&mut self) -> Option<PageRead> {
        if self.reading.is_some() {
            self.again = true;
            return None;
        }
        Some(self.start(Vec::new(), self.items.len(), None))
    }

    /// The page after the last one shown.
    pub fn more(&mut self) -> Option<PageRead> {
        if self.reading.is_some() {
            return None;
        }
        let cursor = self.next_cursor.clone()?;
        let want = self.items.len() + 1;
        Some(self.start(self.items.clone(), want, Some(cursor)))
    }

    fn start(&mut self, held: Vec<BoardSummary>, want: usize, cursor: Option<String>) -> PageRead {
        self.epoch += 1;
        self.reading = Some((held, want));
        self.read(cursor)
    }

    fn read(&self, cursor: Option<String>) -> PageRead {
        let args = BoardPageArgs {
            project: None,
            state: self.filter.clone(),
            cursor,
        };
        PageRead {
            epoch: self.epoch,
            args,
        }
    }

    /// Takes a page's answer. Returns whether it replaced the list, and the read to send next: the next page while the
    /// read needs more cards, or one more read for a change that came meanwhile.
    pub fn apply_page(&mut self, epoch: u64, page: BoardPageResult) -> (bool, Option<PageRead>) {
        if epoch != self.epoch {
            return (false, None);
        }
        let Some((mut held, want)) = self.reading.take() else {
            return (false, None);
        };
        held.extend(page.items);
        if held.len() < want && page.next_cursor.is_some() {
            self.reading = Some((held, want));
            return (false, Some(self.read(page.next_cursor)));
        }
        (self.items, self.counts, self.next_cursor) = (held, Some(page.counts), page.next_cursor);
        let again = std::mem::take(&mut self.again)
            .then(|| self.changed())
            .flatten();
        (true, again)
    }

    /// A read that failed or dropped: the list stays as it was, and the next change reads again.
    pub fn page_failed(&mut self, epoch: u64) {
        if epoch == self.epoch {
            (self.reading, self.again) = (None, false);
        }
    }

    /// Whether a shown card is still going, so its duration needs the clock.
    pub fn ticking(&self) -> bool {
        (self.items.iter()).any(|item| !TERMINAL.contains(&item.task.state.as_str()))
    }

    /// Expands the card, or folds it when it's the one expanded. Returns its detail read.
    pub fn select(&mut self, machine: &str, task: &str) -> Option<BoardTaskArgs> {
        self.detail = None;
        let picked = (machine.to_string(), task.to_string());
        if self.selected.as_ref() == Some(&picked) {
            self.selected = None;
            return None;
        }
        self.selected = Some(picked);
        self.detail_read(None, None)
    }

    /// The topic's update reads the cards again. The topic's state (after a reconnect) reads the expanded card's first
    /// detail page again too, as a change to that card does, since it may have been answered or asked again meanwhile.
    pub fn heard(&mut self, update: Update) -> (Option<PageRead>, Option<BoardTaskArgs>) {
        let detail = match update {
            Update::State(_) => self.detail_read(None, None),
            Update::Change { item, .. } => serde_json::from_value::<BoardChange>(item)
                .ok()
                .and_then(|change| self.refresh_detail(&change)),
        };
        (self.changed(), detail)
    }

    /// The expanded card's detail read again, when `change` is for it.
    pub fn refresh_detail(&self, change: &BoardChange) -> Option<BoardTaskArgs> {
        let (machine, task) = self.selected.as_ref()?;
        (*machine == change.machine_id && *task == change.task_key)
            .then(|| self.detail_read(None, None))
            .flatten()
    }

    /// The expanded card's next page of earlier events.
    pub fn earlier_events(&self) -> Option<BoardTaskArgs> {
        self.detail_read(Some(self.detail.as_ref()?.next_before_seq?), None)
    }

    /// The expanded card's next page of questions.
    pub fn more_questions(&self) -> Option<BoardTaskArgs> {
        let cursor = self.detail.as_ref()?.next_question_cursor.clone();
        self.detail_read(None, Some(cursor?))
    }

    fn detail_read(
        &self,
        before_seq: Option<i64>,
        question_cursor: Option<String>,
    ) -> Option<BoardTaskArgs> {
        let (machine_id, task_key) = self.selected.clone()?;
        Some(BoardTaskArgs {
            machine_id,
            task_key,
            before_seq,
            question_cursor,
        })
    }

    /// Takes a detail read's answer for the expanded card: a later page adds its events or questions, a first page
    /// replaces the detail. One for a card no longer expanded is dropped.
    pub fn apply_detail(&mut self, args: &BoardTaskArgs, result: BoardTaskResult) {
        if self.selected != Some((args.machine_id.clone(), args.task_key.clone())) {
            return;
        }
        match (&mut self.detail, args.before_seq, &args.question_cursor) {
            (Some(detail), Some(_), _) => {
                detail.feed.extend(result.feed);
                detail.next_before_seq = result.next_before_seq;
            }
            (Some(detail), _, Some(_)) => {
                detail.questions.extend(result.questions);
                detail.next_question_cursor = result.next_question_cursor;
            }
            _ => self.detail = Some(result),
        }
    }

    /// The answer to `q` as shown: its own machine, task, question and generation. The same answer to the same
    /// generation sent again after a failure keeps its key; a different answer or generation gets a new GUID key.
    pub fn answer(&mut self, q: &BoardQuestion, value: BoardAnswerValue) -> BoardAnswerArgs {
        let draft = self.drafts.entry(question_id(q)).or_default();
        if draft.value.as_ref() != Some(&value)
            || draft.generation != q.generation
            || draft.answer_key.is_empty()
        {
            draft.answer_key = uuid::Uuid::new_v4().to_string();
        }
        (draft.value, draft.generation) = (Some(value.clone()), q.generation);
        (draft.sending, draft.error) = (true, None);
        BoardAnswerArgs {
            machine_id: q.machine_id.clone(),
            task_key: q.task_key.clone(),
            question_key: q.question_key.clone(),
            generation: q.generation,
            answer_key: draft.answer_key.clone(),
            answer: value,
        }
    }

    /// The answer didn't go: its text and key stay, with why.
    pub fn answer_failed(&mut self, id: &QuestionId, message: String) {
        if let Some(draft) = self.drafts.get_mut(id) {
            (draft.sending, draft.error) = (false, Some(message));
        }
    }

    pub fn answer_sent(&mut self, id: &QuestionId) {
        self.drafts.remove(id);
    }
}

#[cfg(test)]
#[path = "board_state_tests.rs"]
mod tests;

#[cfg(test)]
#[path = "board_ui_check_tests.rs"]
mod ui_check_tests;
