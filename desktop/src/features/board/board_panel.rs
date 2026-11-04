//! The Build Board's drop-down: three tabs over the shared engine store. It exists only while open, so closing releases
//! its watches, rows and clock. What it keeps for next time, the top bar holds in `BoardMemory`.
use std::collections::HashMap;
use std::rc::Rc;

use chrono::{Local, Timelike as _};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, Size, v_flex};
use gpui_kit::*;

use super::board_prefs::{BoardPrefs, BoardTab};
use super::board_queries::{BoardData, badge_count, open_owner_questions, summary_counts};
use super::board_sections::{
    ANSWERED_FOLD, Entry, Section, entries, first_tab, heading, summary, tab_sections,
};
use super::question_row::{QuestionRows, card_key};
use super::run_card::{RunCard, RunCardEvent};
use super::session_tabs::SessionTabs;
use crate::activity::{CardBody, NeedsCard, RunRow};
use crate::core::clock::backend_clock::BackendClock;
use crate::core::clock::tick::Tick;
use crate::core::commands::command_registry::CommandRegistry;
use crate::core::engine::{
    EngineChanged, EngineFeed, EngineKey, EngineScope, EngineWatch, SessionKey,
};
use crate::core::intents::session_open::{
    SessionOpenRequest, SessionOpenResult, SessionOpenTarget,
};
use crate::core::socket::client::SocketClient;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::row_drop_down::{RowDropDownEvent, RowDropDowns};
use uuid::Uuid;

use super::ToggleBoard;

const WIDTH: Pixels = px(720.);
/// How far the panel stays from the window's top and bottom together.
const WINDOW_CLEARANCE: Pixels = px(80.);
/// How far past the viewport the list lays out, a page's worth of cards at most.
const OVERDRAW: Pixels = px(400.);

/// Kept by the top bar across opens for the window's life: where each tab was scrolled to.
#[derive(Clone, Default)]
pub struct BoardMemory {
    scroll: HashMap<usize, ListOffset>,
}

fn tab_index(tab: BoardTab) -> usize {
    BoardTab::ALL
        .iter()
        .position(|held| *held == tab)
        .unwrap_or(0)
}

/// The start of today here, which is where "today" sections cut.
pub fn local_midnight_ms() -> i64 {
    let now = Local::now();
    let midnight = now
        .with_hour(0)
        .and_then(|time| time.with_minute(0))
        .and_then(|time| time.with_second(0))
        .and_then(|time| time.with_nanosecond(0));
    midnight.map_or_else(|| now.timestamp_millis(), |time| time.timestamp_millis())
}

pub struct BoardPanel {
    feed: Entity<EngineFeed>,
    data: Rc<BoardData>,
    dirty: bool,
    prefs: BoardPrefs,
    tab: BoardTab,
    memory: BoardMemory,
    entries: Rc<Vec<Entry>>,
    list: ListState,
    /// A question answered under the pointer stays put until the pointer leaves, so nothing moves under a click.
    hovering: bool,
    shown_open: Vec<EngineKey>,
    questions: Entity<QuestionRows>,
    run_drops: Entity<RowDropDowns>,
    session_drops: Entity<RowDropDowns>,
    run_cards: HashMap<EngineKey, (Entity<RunCard>, Subscription)>,
    session_tabs: HashMap<EngineKey, Entity<SessionTabs>>,
    /// A new run to open once its row arrives.
    pending_run: Option<EngineKey>,
    /// Rows saved open, reopened the first time they're listed.
    reopened: bool,
    watches: HashMap<EngineScope, EngineWatch>,
    clock: Option<Entity<BackendClock>>,
    clock_held: bool,
    drew_running: bool,
    /// Held only until the existing open action accepts or refuses.
    opening: Option<Subscription>,
    open_failed: Option<SharedString>,
    focus: FocusHandle,
    #[cfg(test)]
    built_rows: usize,
    _watching: Vec<Subscription>,
}

impl EventEmitter<DismissEvent> for BoardPanel {}

impl Focusable for BoardPanel {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BoardPanel {
    /// Adds the panel's intent to the palette. The top bar owns its anchored popover.
    pub fn register(cx: &mut App) {
        CommandRegistry::add("Build Board", Box::new(ToggleBoard), cx);
    }

    pub fn new(memory: BoardMemory, cx: &mut Context<Self>) -> Self {
        let feed = EngineFeed::global(cx);
        let data = Rc::new(BoardData::read(feed.read(cx)));
        let prefs = BoardPrefs::read(cx);
        let tab = first_tab(&data, prefs.tab);
        let need_drops = cx.new(|_| RowDropDowns::default());
        let questions = cx.new(|_| QuestionRows::new(feed.clone(), need_drops.clone(), None));
        let (run_drops, session_drops) = (
            cx.new(|_| RowDropDowns::default()),
            cx.new(|_| RowDropDowns::default()),
        );
        let mut watching = vec![
            cx.subscribe(&feed, |panel, _, _: &EngineChanged, cx| {
                panel.dirty = true;
                cx.notify();
            }),
            cx.observe(&questions, |_, _, cx| cx.notify()),
            cx.subscribe(&need_drops, Self::row_moved),
            cx.subscribe(&run_drops, Self::row_moved),
            cx.subscribe(&session_drops, Self::row_moved),
        ];
        if let Some(machines) = Machines::try_global(cx) {
            watching.push(cx.observe(&machines, |_, _, cx| cx.notify()));
        }
        let clock = SocketClient::try_global(cx).map(|_| cx.new(|_| BackendClock::new()));
        if let Some(clock) = &clock {
            watching.push(cx.subscribe(clock, |_, _, _: &Tick, cx| cx.notify()));
        }
        let mut panel = Self {
            feed,
            data,
            dirty: false,
            prefs,
            tab,
            memory,
            entries: Rc::new(Vec::new()),
            list: ListState::new(0, ListAlignment::Top, OVERDRAW),
            hovering: false,
            shown_open: Vec::new(),
            questions,
            run_drops,
            session_drops,
            run_cards: HashMap::new(),
            session_tabs: HashMap::new(),
            pending_run: None,
            reopened: false,
            watches: HashMap::new(),
            clock,
            clock_held: false,
            drew_running: false,
            opening: None,
            open_failed: None,
            focus: cx.focus_handle(),
            #[cfg(test)]
            built_rows: 0,
            _watching: watching,
        };
        panel.rebuild(cx);
        panel.restore_scroll();
        panel
    }

    pub fn memory(&self) -> BoardMemory {
        let mut memory = self.memory.clone();
        memory
            .scroll
            .insert(tab_index(self.tab), self.list.logical_scroll_top());
        memory
    }

    pub fn data(&self) -> Rc<BoardData> {
        self.data.clone()
    }

    pub fn remembered_tab(&self, id: &str) -> Option<super::board_prefs::SessionTab> {
        self.prefs.session_tab(id)
    }

    pub fn remember_session_tab(
        &mut self,
        id: String,
        tab: super::board_prefs::SessionTab,
        cx: &mut Context<Self>,
    ) {
        self.prefs.remember_session_tab(id, tab, cx);
    }

    fn row_moved(
        &mut self,
        _: Entity<RowDropDowns>,
        event: &RowDropDownEvent,
        cx: &mut Context<Self>,
    ) {
        match event {
            RowDropDownEvent::Moved(key) => {
                let at = self.entries.iter().position(|entry| match entry {
                    Entry::Need(row) | Entry::Run(row) | Entry::Session(row) => {
                        card_key(row) == *key
                    }
                    _ => false,
                });
                if let Some(at) = at {
                    self.list.remeasure_items(at..at + 1);
                }
            }
            RowDropDownEvent::Opened(key) => {
                self.prefs.open.insert(key.to_string());
                self.prefs.save_open(cx);
            }
            RowDropDownEvent::Shut(key) => {
                self.prefs.open.remove(key);
                self.prefs.save_open(cx);
            }
        }
        cx.notify();
    }

    /// Reads the shared rows and lists them again. Runs at most once a frame, from drawing.
    fn rebuild(&mut self, cx: &mut Context<Self>) {
        if std::mem::take(&mut self.dirty) {
            self.data = Rc::new(BoardData::read(self.feed.read(cx)));
        }
        self.follow_machines(cx);
        let local =
            SocketClient::try_global(cx).and_then(|socket| socket.read(cx).engine_machine());
        let held = if self.hovering {
            std::mem::take(&mut self.shown_open)
        } else {
            Vec::new()
        };
        let sections = tab_sections(
            &self.data,
            self.tab,
            local_midnight_ms(),
            local.as_deref(),
            &held,
        );
        if let Some(Section::Open(keys)) = sections.first() {
            self.shown_open = keys.clone();
        }
        let rows = entries(&sections, self.tab, &self.prefs);
        self.release_gone_rows(&rows, cx);
        if rows != *self.entries {
            let top = self.list.logical_scroll_top();
            self.list.reset(rows.len());
            self.list.scroll_to(top);
            self.entries = Rc::new(rows);
        } else {
            self.list.remeasure();
        }
        self.open_pending(cx);
    }

    /// The rail holds sessions and needs; only this panel asks for runs and PRs.
    fn follow_machines(&mut self, cx: &mut Context<Self>) {
        for machine in self.feed.read(cx).loaded_machines("sessions") {
            for topic in ["runs", "prs"] {
                let scope = EngineScope {
                    machine: machine.clone(),
                    topic: topic.into(),
                    key: None,
                };
                if self.watches.contains_key(&scope) {
                    continue;
                }
                if let Ok(watch) = self
                    .feed
                    .update(cx, |feed, cx| feed.watch(scope.clone(), cx))
                {
                    self.watches.insert(scope, watch);
                }
            }
        }
    }

    fn release_gone_rows(&mut self, rows: &[Entry], cx: &mut Context<Self>) {
        let listed = |wanted: fn(&Entry) -> Option<&EngineKey>| {
            rows.iter()
                .filter_map(wanted)
                .map(card_key)
                .collect::<std::collections::HashSet<_>>()
        };
        let needs = rows
            .iter()
            .filter_map(|entry| {
                if let Entry::Need(key) = entry {
                    Some(key.clone())
                } else {
                    None
                }
            })
            .collect::<Vec<_>>();
        self.questions
            .update(cx, |rows, cx| rows.retain(&needs, cx));
        // A tab that isn't showing keeps its open row, since selection outlives what's on screen.
        if self.tab == BoardTab::Workflows {
            let runs = listed(|entry| {
                if let Entry::Run(key) = entry {
                    Some(key)
                } else {
                    None
                }
            });
            self.run_drops.update(cx, |drops, cx| {
                drops.retain_keys(|key| runs.contains(key), cx)
            });
        }
        if self.tab == BoardTab::Sessions {
            let sessions = listed(|entry| {
                if let Entry::Session(key) = entry {
                    Some(key)
                } else {
                    None
                }
            });
            self.session_drops.update(cx, |drops, cx| {
                drops.retain_keys(|key| sessions.contains(key), cx)
            });
        }
        let (run_drops, session_drops) = (self.run_drops.read(cx), self.session_drops.read(cx));
        self.run_cards
            .retain(|key, _| run_drops.is_showing(&card_key(key)));
        self.session_tabs
            .retain(|key, _| session_drops.is_showing(&card_key(key)));
    }

    fn restore_scroll(&mut self) {
        if let Some(top) = self.memory.scroll.get(&tab_index(self.tab)) {
            self.list.scroll_to(*top);
        }
    }

    fn toggle_fold(&mut self, id: &'static str, cx: &mut Context<Self>) {
        if self.prefs.unfolded.contains(id) {
            self.prefs.unfolded.remove(id);
        } else {
            self.prefs.unfolded.insert(id.to_owned());
        }
        self.prefs.save_folds(cx);
        self.rebuild(cx);
        cx.notify();
    }

    fn run_body(this: WeakEntity<Self>, key: EngineKey) -> CardBody {
        Rc::new(move |_, cx| {
            let Some(this) = this.upgrade() else {
                return Empty.into_any_element();
            };
            let (card, data) = this.update(cx, |panel, cx| {
                (panel.run_card(&key, cx), panel.data.clone())
            });
            card.update(cx, |card, cx| card.draw(&data, cx))
        })
    }

    fn run_card(&mut self, key: &EngineKey, cx: &mut Context<Self>) -> Entity<RunCard> {
        if let Some((card, _)) = self.run_cards.get(key) {
            return card.clone();
        }
        let card = cx.new(|cx| RunCard::new(key.clone(), cx));
        let watched = cx.observe(&card, |_, _, cx| cx.notify());
        let selected = cx.subscribe(&card, |panel, _, event: &RunCardEvent, cx| {
            let RunCardEvent::Select(key) = event;
            panel.select_run(key.clone(), cx);
        });
        self._watching.push(selected);
        self.run_cards.insert(key.clone(), (card.clone(), watched));
        card
    }

    fn session_body(this: WeakEntity<Self>, key: EngineKey) -> CardBody {
        Rc::new(move |_, cx| {
            let Some(this) = this.upgrade() else {
                return Empty.into_any_element();
            };
            this.update(cx, |panel, cx| {
                let session = SessionKey {
                    machine: key.machine.clone(),
                    id: key.id.clone(),
                };
                let selected = panel.prefs.session_tab(&card_key(&key));
                let host = cx.weak_entity();
                let tabs = panel
                    .session_tabs
                    .entry(key.clone())
                    .or_insert_with(|| cx.new(|cx| SessionTabs::new(session, host, selected, cx)));
                tabs.clone().into_any_element()
            })
        })
    }

    /// Opens a session with the app's own action, and closes the panel once it's accepted.
    pub fn open_session(&mut self, key: SessionKey, window: &mut Window, cx: &mut Context<Self>) {
        let request_id = Uuid::new_v4().to_string();
        let expected = request_id.clone();
        self.opening = Some(cx.subscribe(
            &SelectedSession::global(cx),
            move |panel, _, event: &SessionOpenResult, cx| {
                if event.request_id != expected {
                    return;
                }
                panel.opening = None;
                match &event.result {
                    Ok(()) => cx.emit(DismissEvent),
                    Err(error) => {
                        panel.open_failed = Some(error.message.clone().into());
                        cx.notify();
                    }
                }
            },
        ));
        window.dispatch_action(
            Box::new(SessionOpenRequest {
                key,
                request_id,
                target: SessionOpenTarget::Engine,
            }),
            cx,
        );
    }

    /// Times count only while something counting is on screen.
    fn hold_clock_if_needed(&mut self, cx: &mut Context<Self>) {
        if self.drew_running && !self.clock_held {
            self.clock_held = true;
            if let Some(clock) = &self.clock {
                clock.update(cx, |clock, cx| clock.hold(cx));
            }
        }
    }

    fn release_clock_if_idle(&mut self, cx: &mut Context<Self>) {
        if self.clock_held && !std::mem::take(&mut self.drew_running) {
            self.clock_held = false;
            if let Some(clock) = &self.clock {
                clock.update(cx, |clock, cx| clock.release(cx));
            }
        }
    }

    #[cfg(test)]
    pub(super) fn test_set_data(&mut self, data: BoardData, cx: &mut Context<Self>) {
        self.data = Rc::new(data);
        self.dirty = false;
        self.rebuild(cx);
        cx.notify();
    }

    #[cfg(test)]
    pub(super) fn test_questions(&self) -> Entity<QuestionRows> {
        self.questions.clone()
    }

    #[cfg(test)]
    pub(super) fn test_list(&self) -> ListState {
        self.list.clone()
    }

    #[cfg(test)]
    pub(super) fn test_session_tabs(&self, key: &EngineKey) -> Option<Entity<SessionTabs>> {
        self.session_tabs.get(key).cloned()
    }

    #[cfg(test)]
    pub(super) fn test_run_card_held(&self, key: &EngineKey) -> bool {
        self.run_cards.contains_key(key)
    }

    #[cfg(test)]
    pub(super) fn test_open(&mut self, key: &EngineKey, cx: &mut Context<Self>) {
        match key.kind.as_str() {
            "run" => self.open_run(key, cx),
            _ => self.open_session_row(key, cx),
        }
    }

    #[cfg(test)]
    pub(super) fn test_show(&mut self, tab: BoardTab, cx: &mut Context<Self>) {
        self.show_tab(tab, cx);
    }

    #[cfg(test)]
    pub(super) fn test_state(&self) -> (usize, bool, BoardTab) {
        (self.built_rows, self.clock_held, self.tab)
    }

    #[cfg(test)]
    pub(super) fn test_reset_built(&mut self) {
        self.built_rows = 0;
    }
}

/// The dot's count on the top bar, from the same rows the panel lists.
pub fn top_bar_badge(feed: &EngineFeed) -> Option<SharedString> {
    let count = badge_count(&BoardData::read(feed));
    super::board_queries::badge_label(count).map(Into::into)
}

#[path = "board_open.rs"]
mod board_open;
#[path = "board_panel_render.rs"]
mod board_panel_render;
#[path = "board_rows.rs"]
mod board_rows;

#[cfg(test)]
#[path = "board_panel_tests.rs"]
mod tests;
