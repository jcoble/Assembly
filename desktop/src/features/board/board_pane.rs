//! `BoardPane` is the Build Board tab beside Chat (board#5, board#8). It subscribes to the `board` topic before it
//! reads, and reads the cards once the topic's state comes, at open and after each reconnect, so no change between the
//! two is missed. Each change reads the cards again (`BoardState`) and keeps the reader's top card where it was. The
//! tab holds its topic, its reads, its answers on their way and the backend clock (only while a shown card is still
//! going), so closing it drops them all; the backend keeps mirroring. The top bar's Board button and the palette's
//! "Build Board" open it. Replaces the old app's Board popover.

use std::collections::HashMap;

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, PanelInfo, PanelState, panel_handle, register_panel,
};
use gpui_kit::component::input::InputState;
use gpui_kit::component::{ActiveTheme as _, Selectable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;
use serde_json::Value;

use super::OpenBoard;
use super::board_card::{self, STATES};
use super::board_payload::{
    BoardAnswerValue, BoardChange, BoardPageResult, BoardQuestion, BoardTaskArgs, BoardTaskResult,
};
use super::board_state::{BoardState, PageRead, QuestionId, question_id};
use crate::core::clock::backend_clock::BackendClock;
use crate::core::clock::tick::Tick;
use crate::core::commands::command_registry::CommandRegistry;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::update::Update;
use crate::core::state::machines::Machines;
use crate::core::ui::center_panes::CenterPanes;

/// The pane's name in the saved layout, its one key in the center, and its palette label.
pub const BOARD: &str = "Build Board";

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
}

impl BoardPane {
    /// Restores the tab from the saved layout, adds "Build Board" to the palette, and owns `OpenBoard`.
    pub fn register(cx: &mut App) {
        register_panel(cx, BOARD, |_, _, cx| {
            let pane = cx.new(BoardPane::new);
            CenterPanes::remember(BOARD, PanelId::from(pane.entity_id()), cx);
            panel_handle(pane)
        });
        CommandRegistry::add(BOARD, Box::new(OpenBoard), cx);
        cx.on_action(|_: &OpenBoard, cx| {
            // As the open diff intent: the window is updated once the click that dispatched this is done.
            if let Some(window) = cx
                .active_window()
                .or_else(|| cx.windows().into_iter().next())
            {
                cx.defer(move |cx| {
                    _ = window.update(cx, |_, window, cx| {
                        CenterPanes::open(BOARD, window, cx, |_, cx| cx.new(BoardPane::new))
                    })
                });
            }
        });
    }

    pub fn new(cx: &mut Context<Self>) -> Self {
        let _machines =
            Machines::try_global(cx).map(|machines| cx.observe(&machines, |_, _, cx| cx.notify()));
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
        };
        match SocketClient::try_global(cx) {
            Some(socket) => pane.listen(socket, cx),
            None => pane.failed = Some("The backend isn't running.".into()),
        }
        pane
    }

    /// The topic's state (at subscribe and after a reconnect) and each change read the cards again; a change to the
    /// expanded card reads its detail again too.
    fn listen(&mut self, socket: Entity<SocketClient>, cx: &mut Context<Self>) {
        let mut topic = socket.read(cx).subscribe("board", None);
        self._topic = Some(cx.spawn(async move |pane, cx| {
            while let Ok(update) = topic.updates.recv().await {
                let heard = pane.update(cx, |pane, cx| {
                    let read = pane.state.changed();
                    pane.read_page(read, cx);
                    if let Update::Change { item, .. } = update
                        && let Ok(change) = serde_json::from_value::<BoardChange>(item)
                    {
                        let read = pane.state.refresh_detail(&change);
                        pane.read_detail(read, cx);
                    }
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
        let anchor = (self.state.items.get(top.item_ix))
            .map(|item| (item.task.machine_id.clone(), item.task.task_key.clone()));
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

    fn filter(&mut self, filter: Option<&str>, cx: &mut Context<Self>) {
        let read = self.state.set_filter(filter.map(String::from));
        self.list.reset(0);
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
            .call::<Value>("board", "answer", value, None);
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

impl EventEmitter<PanelEvent> for BoardPane {}

impl Focusable for BoardPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for BoardPane {
    fn panel_name(&self) -> &'static str {
        BOARD
    }

    fn dump(&self, _: &App) -> PanelState {
        PanelState {
            panel_name: BOARD.into(),
            children: Vec::new(),
            info: PanelInfo::panel(Value::Null),
        }
    }
}

impl Panel for BoardPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        BOARD
    }
}

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
            .child(header)
            .child(body)
            .children(more)
    }
}
