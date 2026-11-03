//! `BoardPane` is the Build Board dropdown body (board#5, board#8). It subscribes to the `board` topic before it
//! reads, and reads the cards once the topic's state comes, at open and after each reconnect, so no change between the
//! two is missed. Each change reads the cards again (`BoardState`) and keeps the reader's top card where it was. The
//! body holds its topic, its reads, its answers on their way and the backend clock (only while a shown card is still
//! going), so dismissing it drops them all; the backend keeps mirroring. The top bar's existing popover owns its
//! lifetime, and the palette's "Build Board" opens that same dropdown.

use std::collections::HashMap;

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{DockAreaState, DockState, PanelInfo, PanelState};
use gpui_kit::component::input::InputState;
use gpui_kit::component::{ActiveTheme as _, Selectable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

use super::OpenBoard;
use super::board_card::{self, STATES};
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
            .filter_map(|(index, child)| {
                prune(child).map(|child| {
                    kept.push(index);
                    child
                })
            })
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
        let mut pane = BoardPane {
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
            cx.notify();
        }
        self.read_page(next, cx);
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

    /// Expands the card, or folds it when it's the one expanded.
    pub(super) fn select(&mut self, machine: &str, task: &str, cx: &mut Context<Self>) {
        (self._detail, self.detail_failed) = (None, None);
        let read = self.state.select(machine, task);
        self.read_detail(read, cx);
        self.redraw(cx);
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
        let selected = SelectedSession::global(cx);
        let requested = id.to_string();
        self.opening = Some(cx.observe(&selected, move |pane, selected, cx| {
            if selected.read(cx).session_id() == Some(requested.as_str()) {
                pane.opening = None;
                cx.emit(DismissEvent);
            }
        }));
        window.dispatch_action(Box::new(OpenSession { id: id.into() }), cx);
        let requested = id.to_string();
        cx.defer_in(window, move |pane, _, cx| {
            // OpenSession commits SelectedSession in its action handler; an already selected target is accepted too.
            // This confirms navigation's selection only, never that the backend loaded the session successfully.
            pane.opening = None;
            if selected.read(cx).session_id() == Some(requested.as_str()) {
                cx.emit(DismissEvent);
            } else {
                pane.detail_failed = Some("Could not open the session.".into());
                pane.redraw(cx);
            }
        });
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

impl Render for BoardPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let counts = self.state.counts.clone();
        let headline = match counts.as_ref().map_or(0, |counts| counts.needs_you) {
            0 => "Nothing needs you".to_string(),
            1 => "1 thing needs you".to_string(),
            n => format!("{n} things need you"),
        };
        let filters = [("All", None)]
            .into_iter()
            .chain(STATES.iter().map(|(state, label)| (*label, Some(*state))))
            .map(|(label, state)| {
                Button::new(label)
                    .ghost()
                    .small()
                    .label(label)
                    .selected(self.state.filter.as_deref() == state)
                    .on_click(cx.listener(move |pane, _, _, cx| pane.filter(state, cx)))
            });
        let header = v_flex()
            .px_4()
            .pt_3()
            .pb_2()
            .gap_0p5()
            .child(div().text_xs().text_color(muted).child("BUILD BOARD"))
            .child(
                div()
                    .text_lg()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(headline),
            )
            .children(counts.as_ref().map(|c| {
                let line = format!(
                    "{} running · {} finished · {} in all",
                    c.running, c.terminal, c.total
                );
                div().text_sm().text_color(muted).child(line)
            }))
            .child(h_flex().pt_1().gap_1().flex_wrap().children(filters));
        let note = |text: String| div().px_4().py_2().text_sm().text_color(muted).child(text);
        let body = match &self.failed {
            Some(message) => note(message.clone()).text_color(danger).into_any_element(),
            None if counts.is_none() => note("Reading the Board…".into()).into_any_element(),
            None if self.state.items.is_empty() => note(match self.state.filter {
                None => "No agent has reported a task yet.".into(),
                Some(_) => "No tasks in this state.".into(),
            })
            .into_any_element(),
            None => list(self.list.clone(), cx.processor(board_card::card))
                .flex_1()
                .min_h_0()
                .px_3()
                .into_any_element(),
        };
        let more = (self.state.next_cursor.is_some()).then(|| {
            let shown = self.state.items.len();
            let total = counts.as_ref().map_or(0, |counts| counts.total);
            h_flex()
                .px_4()
                .py_2()
                .gap_2()
                .text_sm()
                .text_color(muted)
                .child(format!("Showing {shown} of {total}"))
                .child(
                    Button::new("board-more")
                        .ghost()
                        .small()
                        .label("Load more")
                        .on_click(cx.listener(|pane, _, _, cx| pane.more(cx))),
                )
        });
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .min_h_0()
            .child(header)
            .child(body)
            .children(more)
    }
}
