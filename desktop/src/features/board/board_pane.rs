//! `BoardPane` is the Build Board dropdown body (board#5, board#8). It subscribes to the `board` topic before it
//! reads, and reads the cards once the topic's state comes, at open and after each reconnect, so no change between the
//! two is missed. Each change reads the cards again (`BoardState`) and keeps the reader's top card where it was. The
//! body holds its topic, its reads, its answers on their way and the backend clock (only while a shown card is still
//! going), so dismissing it drops them all; the backend keeps mirroring. The top bar's existing popover owns its
//! lifetime, and the palette's "Build Board" opens that same dropdown.

use std::collections::{HashMap, HashSet};

use gpui_kit::component::dock::{DockAreaState, DockState, PanelInfo, PanelState};
use gpui_kit::component::input::InputState;
use gpui_kit::*;

use super::OpenBoard;
use super::board_card;
use super::board_payload::{
    BoardAnswerResult, BoardAnswerValue, BoardPageResult, BoardQuestion, BoardTaskArgs,
    BoardTaskResult,
};
use super::board_state::{BoardState, PageRead, QuestionId, question_id};
use crate::core::clock::backend_clock::BackendClock;
use crate::core::clock::tick::Tick;
use crate::core::commands::command_registry::CommandRegistry;
use crate::core::intents::open_session::OpenSession;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::row_drop_down::{RowDropDownEvent, RowDropDowns};

/// The retired saved pane name and the palette's label.
pub const BOARD: &str = "Build Board";

/// Removes only the retired Board tabs from an otherwise compatible saved layout.
pub fn remove_saved_panel(state: &mut DockAreaState) {
    fn prune(mut panel: PanelState) -> Option<PanelState> {
        if panel.panel_name == BOARD {
            return None;
        }
        let old_len = panel.children.len();
        let mut kept = Vec::new();
        panel.children = panel
            .children
            .into_iter()
            .enumerate()
            .filter_map(|(index, child)| prune(child).inspect(|_| kept.push(index)))
            .collect();
        if kept.len() != old_len {
            match &mut panel.info {
                PanelInfo::Tabs { active_index } => {
                    *active_index = kept
                        .iter()
                        .position(|index| *index == *active_index)
                        .unwrap_or_else(|| {
                            kept.iter()
                                .take_while(|index| **index < *active_index)
                                .count()
                                .min(kept.len().saturating_sub(1))
                        });
                }
                PanelInfo::Stack { sizes, .. } => {
                    *sizes = kept
                        .iter()
                        .filter_map(|index| sizes.get(*index).copied())
                        .collect();
                }
                PanelInfo::Panel(_) => {}
            }
            if kept.is_empty()
                && matches!(panel.info, PanelInfo::Tabs { .. } | PanelInfo::Stack { .. })
            {
                return None;
            }
        }
        Some(panel)
    }
    state.center = prune(std::mem::take(&mut state.center)).unwrap_or_else(|| PanelState {
        panel_name: "TabPanel".into(),
        children: Vec::new(),
        info: PanelInfo::tabs(0),
    });
    for dock in [
        &mut state.left_dock,
        &mut state.right_dock,
        &mut state.bottom_dock,
    ] {
        *dock = dock.take().and_then(|saved| {
            prune(saved.panel().clone())
                .map(|panel| DockState::new(panel, saved.placement(), saved.size(), saved.open()))
        });
    }
}

pub struct BoardPane {
    focus: FocusHandle,
    pub(super) state: BoardState,
    list: ListState,
    /// Why the last page read failed, until one lands.
    failed: Option<String>,
    pub(super) detail_failed: Option<String>,
    /// The backend clock's last tick, for the durations of cards still going.
    pub(super) now: Option<i64>,
    /// The one open card, growing and shrinking in the list.
    pub(super) drops: Entity<RowDropDowns>,
    _drops: Subscription,
    /// The last card shut, and the detail it had.
    closing: Option<((String, String), Option<BoardTaskResult>)>,
    inputs: HashMap<QuestionId, Entity<InputState>>,
    _topic: Option<Task<()>>,
    _page: Option<Task<()>>,
    _detail: Option<Task<()>>,
    answers: HashMap<QuestionId, Task<()>>,
    clock: Option<(Entity<BackendClock>, Subscription)>,
    /// Redraws the offline labels when a machine goes up or down.
    _machines: Option<Subscription>,
    /// Held only until the existing navigation action accepts or refuses the requested selection.
    opening: Option<Subscription>,
}

impl BoardPane {
    /// Adds the existing dropdown intent to the palette. The top bar owns its anchored popover.
    pub fn register(cx: &mut App) {
        CommandRegistry::add(BOARD, Box::new(OpenBoard), cx);
    }

    pub fn new(cx: &mut Context<Self>) -> Self {
        let _machines = Machines::try_global(cx).map(|machines| {
            cx.observe(&machines, |pane, machines, cx| {
                let offline = machines
                    .read(cx)
                    .machines()
                    .filter(|machine| machine.didnt_answer())
                    .filter_map(|machine| machine.name.clone())
                    .collect();
                let read = pane.state.machines_changed(offline);
                pane.read_detail(read, cx);
                cx.notify();
            })
        });
        let drops = cx.new(|_| RowDropDowns::default());
        let _drops = cx.subscribe(&drops, |pane, _, event: &RowDropDownEvent, cx| {
            if let RowDropDownEvent::Moved(key) = event {
                let card = (pane.state.items.iter()).position(|item| {
                    board_card::card_key(&item.task.machine_id, &item.task.task_key) == *key
                });
                if let Some(at) = card {
                    pane.list.remeasure_items(at..at + 1);
                }
            }
            cx.notify();
        });
        let mut pane = BoardPane {
            drops,
            _drops,
            closing: None,
            focus: cx.focus_handle(),
            state: BoardState::default(),
            list: ListState::new(0, ListAlignment::Top, px(400.)),
            failed: None,
            detail_failed: None,
            now: None,
            inputs: HashMap::new(),
            _topic: None,
            _page: None,
            _detail: None,
            answers: HashMap::new(),
            clock: None,
            _machines,
            opening: None,
        };
        if let Some(machines) = Machines::try_global(cx) {
            pane.state.machines_changed(
                machines
                    .read(cx)
                    .machines()
                    .filter(|machine| machine.didnt_answer())
                    .filter_map(|machine| machine.name.clone())
                    .collect(),
            );
        }
        match SocketClient::try_global(cx) {
            Some(socket) => pane.listen(socket, cx),
            None => pane.failed = Some("The backend isn't running.".into()),
        }
        pane
    }

    /// The topic's state (at subscribe and after a reconnect) and each change read the cards again; the state and a
    /// change to the expanded card read its detail again too.
    fn listen(&mut self, socket: Entity<SocketClient>, cx: &mut Context<Self>) {
        let mut topic = socket.read(cx).subscribe("board", None);
        self._topic = Some(cx.spawn(async move |pane, cx| {
            while let Ok(update) = topic.updates.recv().await {
                let heard = pane.update(cx, |pane, cx| {
                    let (page, detail) = pane.state.heard(update);
                    pane.read_page(page, cx);
                    pane.read_detail(detail, cx);
                });
                if heard.is_err() {
                    return;
                }
            }
        }));
    }

    fn read_page(&mut self, read: Option<PageRead>, cx: &mut Context<Self>) {
        let (Some(read), Some(socket)) = (read, SocketClient::try_global(cx)) else {
            return;
        };
        let args = serde_json::to_value(&read.args).unwrap_or_default();
        // Keyed, so a newer read cancels this one on the backend.
        let call = socket
            .read(cx)
            .call("board", "page", args, Some("board/page"));
        self._page = Some(cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            _ = pane.update(cx, |pane, cx| pane.paged(read.epoch, outcome, cx));
        }));
    }

    fn paged(&mut self, epoch: u64, outcome: Outcome<BoardPageResult>, cx: &mut Context<Self>) {
        let page = match outcome {
            Outcome::Ok(page) => page,
            Outcome::Failed(failure) => {
                self.state.page_failed(epoch);
                self.failed = Some(failure.message);
                return cx.notify();
            }
            Outcome::Aborted => return self.state.page_failed(epoch),
        };
        // The card at the top of the list, and how far into it, so the swap keeps the reader's place.
        let top = self.list.logical_scroll_top();
        let anchor = self.state.scroll_anchor(top);
        let (replaced, next) = self.state.apply_page(epoch, page);
        if replaced {
            self.failed = None;
            self.list.reset(self.state.items.len());
            let found = (self.state.items.iter()).position(|item| {
                anchor.as_ref().is_some_and(|(machine, task)| {
                    item.task.machine_id == *machine && item.task.task_key == *task
                })
            });
            if let Some(item_ix) = found {
                let offset_in_item = top.offset_in_item;
                self.list.scroll_to(ListOffset {
                    item_ix,
                    offset_in_item,
                });
            }
            self.hold_clock(cx);
            self.release_gone_cards(cx);
            cx.notify();
        }
        self.read_page(next, cx);
    }

    /// A card no longer listed never finishes closing, so its body goes now. The selected card stays open: it's still
    /// expanded when a filter brings it back.
    fn release_gone_cards(&mut self, cx: &mut Context<Self>) {
        let mut listed: HashSet<SharedString> = (self.state.items.iter())
            .map(|item| board_card::card_key(&item.task.machine_id, &item.task.task_key))
            .collect();
        listed.extend(
            (self.state.selected.as_ref()).map(|card| board_card::card_key(&card.0, &card.1)),
        );
        self.drops.update(cx, |drops, cx| {
            drops.retain_keys(|key| listed.contains(key), cx)
        });
    }

    /// Holds the backend clock while a shown card is still going, and lets it go otherwise.
    fn hold_clock(&mut self, cx: &mut Context<Self>) {
        if !self.state.ticking() {
            self.clock = None;
            return;
        }
        if self.clock.is_some() {
            return;
        }
        let clock = cx.new(|cx| {
            let mut clock = BackendClock::new();
            clock.hold(cx);
            clock
        });
        let heard = cx.subscribe(&clock, |pane, _, tick: &Tick, cx| {
            pane.now = Some(tick.now);
            cx.notify();
        });
        self.clock = Some((clock, heard));
    }

    fn read_detail(&mut self, read: Option<BoardTaskArgs>, cx: &mut Context<Self>) {
        let (Some(args), Some(socket)) = (read, SocketClient::try_global(cx)) else {
            return;
        };
        let value = serde_json::to_value(&args).unwrap_or_default();
        let call = socket
            .read(cx)
            .call("board", "task", value, Some("board/task"));
        self._detail = Some(cx.spawn(async move |pane, cx| {
            let outcome: Outcome<BoardTaskResult> = call.await;
            _ = pane.update(cx, |pane, cx| {
                match outcome {
                    Outcome::Ok(result) => {
                        pane.detail_failed = None;
                        pane.state.apply_detail(&args, result);
                    }
                    Outcome::Failed(failure) => pane.detail_failed = Some(failure.message),
                    Outcome::Aborted => {}
                }
                pane.redraw(cx);
            });
        }));
    }

    /// Expands the card, or folds it when it's the one expanded. The card on its way shut keeps its detail until the
    /// next card is picked, so it doesn't change as it closes.
    pub(super) fn select(
        &mut self,
        machine: &str,
        task: &str,
        session: &str,
        cx: &mut Context<Self>,
    ) {
        (self._detail, self.detail_failed) = (None, None);
        let was = self.state.selected.clone();
        let had = self.state.detail.take();
        let read = self.state.select(machine, task);
        self.closing = was.map(|card| (card, had));
        self.read_detail(read, cx);
        if self.state.selected.is_none() {
            self.drops.update(cx, |drops, cx| drops.shut(cx));
            return self.redraw(cx);
        }
        let pane = cx.weak_entity();
        let (key, machine, task, session) = (
            board_card::card_key(machine, task),
            machine.to_string(),
            task.to_string(),
            session.to_string(),
        );
        self.drops.update(cx, |drops, cx| {
            drops.toggle(
                key,
                move |window, cx| {
                    pane.update(cx, |pane, cx| {
                        board_card::detail(pane, &machine, &task, &session, window, cx)
                    })
                    .unwrap_or_else(|_| Empty.into_any_element())
                },
                cx,
            )
        });
        self.redraw(cx);
    }

    /// The detail of the card on its way shut.
    pub(super) fn closing_detail(&self, machine: &str, task: &str) -> Option<BoardTaskResult> {
        let (card, detail) = self.closing.as_ref()?;
        (card.0 == machine && card.1 == task)
            .then(|| detail.clone())
            .flatten()
    }

    pub(super) fn earlier_events(&mut self, cx: &mut Context<Self>) {
        let read = self.state.earlier_events();
        self.read_detail(read, cx);
    }

    pub(super) fn more_questions(&mut self, cx: &mut Context<Self>) {
        let read = self.state.more_questions();
        self.read_detail(read, cx);
    }

    pub(super) fn open_session(&mut self, id: &str, window: &mut Window, cx: &mut Context<Self>) {
        let Some(task) = self.state.items.iter().map(|item| &item.task).find(|task| {
            task.creator_session_id == id
                && self.state.selected.as_ref()
                    == Some(&(task.machine_id.clone(), task.task_key.clone()))
        }) else {
            self.detail_failed = Some("The session's current Board source is unavailable.".into());
            self.redraw(cx);
            return;
        };
        // Board's persisted machine_id contract uses `local` only for the owning backend route.
        let route_machine = (task.machine_id != "local").then(|| task.machine_id.clone());
        let request_id = uuid::Uuid::new_v4().to_string();
        let expected = request_id.clone();
        self.opening = Some(cx.subscribe(
            &SelectedSession::global(cx),
            move |pane, _, event: &crate::core::intents::session_open::SessionOpenResult, cx| {
                if event.request_id != expected {
                    return;
                }
                pane.opening = None;
                match &event.result {
                    Ok(()) => cx.emit(DismissEvent),
                    Err(error) => {
                        pane.detail_failed = Some(error.message.clone());
                        pane.redraw(cx);
                    }
                }
            },
        ));
        window.dispatch_action(
            Box::new(OpenSession {
                id: id.into(),
                route_machine,
                request_id,
                historical: false,
            }),
            cx,
        );
    }

    pub(super) fn filter(&mut self, filter: Option<&str>, cx: &mut Context<Self>) {
        let read = self.state.set_filter(filter.map(String::from));
        self.list.reset(0);
        // No card shows until the read lands, so the clock goes now, even if that read fails.
        self.hold_clock(cx);
        self.read_page(Some(read), cx);
        cx.notify();
    }

    fn more(&mut self, cx: &mut Context<Self>) {
        let read = self.state.more();
        self.read_page(read, cx);
    }

    /// Sends the answer to `q` as shown. A sent answer clears its input; a failed one keeps it, and says why.
    pub(super) fn send(
        &mut self,
        q: &BoardQuestion,
        answer: BoardAnswerValue,
        cx: &mut Context<Self>,
    ) {
        let id = question_id(q);
        let args = self.state.answer(q, answer);
        let Some(socket) = SocketClient::try_global(cx) else {
            self.state
                .answer_failed(&id, "The backend isn't running.".into());
            return self.redraw(cx);
        };
        let value = serde_json::to_value(&args).unwrap_or_default();
        let call = socket
            .read(cx)
            .call::<BoardAnswerResult>("board", "answer", value, None);
        let sent = id.clone();
        let task = cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            _ = pane.update(cx, |pane, cx| {
                match outcome {
                    Outcome::Ok(_) => {
                        pane.state.answer_sent(&sent);
                        pane.inputs.remove(&sent);
                    }
                    Outcome::Failed(failure) => pane.state.answer_failed(&sent, failure.message),
                    Outcome::Aborted => pane.state.answer_failed(
                        &sent,
                        "The connection dropped before the answer was confirmed.".into(),
                    ),
                }
                pane.answers.remove(&sent);
                pane.redraw(cx);
            });
        });
        self.answers.insert(id, task);
        self.redraw(cx);
    }

    /// The answer input for `q`, made the first time it's drawn.
    pub(super) fn input(
        &mut self,
        q: &BoardQuestion,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Entity<InputState> {
        (self.inputs.entry(question_id(q)))
            .or_insert_with(|| {
                cx.new(|cx| InputState::new(window, cx).placeholder("Type an answer…"))
            })
            .clone()
    }

    /// A card's height changed: the list measures its cards again.
    fn redraw(&mut self, cx: &mut Context<Self>) {
        self.list.remeasure();
        cx.notify();
    }
}

impl Focusable for BoardPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl EventEmitter<DismissEvent> for BoardPane {}

#[path = "board_pane_render.rs"]
mod render;

#[cfg(test)]
#[path = "board_pane_tests.rs"]
mod tests;
