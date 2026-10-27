//! `SessionPane` owns the open session: the selected session's id (from the selected-session state lane g0-socket
//! owns), its `SessionView`, the rows subscription and the loads it started, the timeline and the composer. Opening
//! another session drops the old subscription and loads, so a stale load stops. While a session is on screen it
//! tells the backend the newest seq shown (`sessions/read`, once the base lane's session summaries land). It registers under the chat pane's
//! name through the core registry (lane g0-host, names from lane g0-shape), never by importing another feature.
//! Replaces Conversation.svelte and the spike's session/panel.rs.

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use std::collections::{HashMap, HashSet};
use std::rc::Rc;

use serde_json::{Value, json};

use super::session_action::OnAction;
use super::timeline::{RenderRow, Timeline, TimelineEvent};
use crate::features::session::client::session_client::{
    CallError, RowUpdate, RowsSink, SessionClient,
};
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::composer::composer_view::{Composer, ComposerEvent};
use crate::features::session::model::change::Change;
use crate::features::session::model::entries::Entry;
use crate::features::session::model::fold::Fold;
use crate::features::session::model::follow::FollowEvent;
use crate::features::session::model::item::ItemKind;
use crate::features::session::model::kept::Kept;
use crate::features::session::model::session_view::SessionView;
use crate::features::session::rows::row::Row;
use crate::features::session::rows::row_page::RowPage;

pub struct SessionPane {
    client: Rc<dyn SessionClient>,
    focus: FocusHandle,
    session: Option<String>,
    view: SessionView,
    /// The run sent from here, or the page's active run on open, until its turn row arrives.
    known_run: Option<String>,
    /// Each finished run's fold, by run id, as of the last refresh.
    folds: HashMap<String, Fold>,
    /// The folds the reader opened, by run id.
    open_folds: HashSet<String>,
    /// What each item keeps while off screen, by item key.
    kept: HashMap<String, Kept>,
    timeline: Entity<Timeline>,
    composer: Entity<Composer>,
    /// The streaming reply's reveal, stepped once a frame while it's behind; `None` at rest.
    reveal: Option<crate::features::session::model::reveal::Reveal>,
    /// Live rows that came before the first page, applied once it lands: a delta needs the blocks the page has.
    /// `None` once the first page is in.
    held: Option<Vec<Row>>,
    /// An older page is loading.
    loading: bool,
    /// The `before` of the last older page that failed: it isn't asked for again by itself, so a failure can't loop.
    failed_before: Option<i64>,
    /// Why the last load failed; empty when there's nothing to say.
    status: SharedString,
    /// The rows subscription and every load this session started; replaced on open, dropped on close. Each call keys
    /// on the session, so a load for the session left behind stops.
    _rows: Option<Subscription>,
    _tasks: Vec<Task<()>>,
    /// The composer's events and the timeline's scrolling.
    _heard: [Subscription; 2],
}

impl SessionPane {
    /// Registers the session pane as the layout's Chat pane, over the socket client. It runs after the placeholders
    /// register, since the last registration for a name wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, "Chat", |_, window, cx| {
            panel_handle(cx.new(|cx| SessionPane::new(Rc::new(SocketSessionClient), window, cx)))
        });
    }

    pub fn new(client: Rc<dyn SessionClient>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let pane = cx.weak_entity();
        let on_action: OnAction = {
            let pane = pane.clone();
            Rc::new(move |action, window, cx| {
                pane.update(cx, |pane, cx| pane.on_action(action, window, cx))
                    .ok();
            })
        };
        let render_row: RenderRow = Rc::new(move |entry, _, cx| {
            pane.upgrade().map_or_else(
                || div().into_any_element(),
                |pane| pane.read(cx).row(entry, &on_action, cx),
            )
        });
        let timeline = cx.new(|cx| Timeline::new(render_row, cx));
        let composer = cx.new(|cx| Composer::new(client.clone(), window, cx));
        let heard = [
            cx.subscribe(&composer, |pane, _, event, cx| match event {
                ComposerEvent::Sending { .. } => pane
                    .timeline
                    .update(cx, |timeline, cx| timeline.follow(FollowEvent::Send, cx)),
                ComposerEvent::Sent { session, run_id }
                    if pane.session.as_ref() == Some(session) =>
                {
                    pane.known_run = Some(run_id.clone());
                    pane.refresh(Change::Nothing, cx);
                }
                // A session left before its send answered gets its run from its rows when it's open again.
                ComposerEvent::Sent { .. } => {}
            }),
            cx.subscribe(&timeline, |pane, _, TimelineEvent::NearTop, cx| {
                pane.load_older(cx)
            }),
        ];
        SessionPane {
            client,
            focus: cx.focus_handle(),
            session: None,
            view: SessionView::default(),
            known_run: None,
            folds: HashMap::new(),
            open_folds: HashSet::new(),
            kept: HashMap::new(),
            timeline,
            composer,
            reveal: None,
            held: None,
            loading: false,
            failed_before: None,
            status: SharedString::default(),
            _rows: None,
            _tasks: Vec::new(),
            _heard: heard,
        }
    }

    /// Shows a session: subscribes to its rows first, then, on the `Jump` that says its seq, loads the page below
    /// `seq + 1`, so the page and the live rows never overlap. A session with no rows yet (a new one) is ready at
    /// once, empty, so its first message sends.
    pub fn open(&mut self, session: String, window: &mut Window, cx: &mut Context<Self>) {
        if self.session.as_ref() == Some(&session) {
            return;
        }
        // The old subscription and loads drop first, so nothing of the session left behind lands here.
        self._rows = None;
        self._tasks.clear();
        self.view = SessionView::default();
        self.held = Some(Vec::new());
        self.open_folds.clear();
        self.kept.clear();
        (
            self.known_run,
            self.loading,
            self.failed_before,
            self.status,
        ) = (None, false, None, SharedString::default());
        let pane = cx.weak_entity();
        let sink: RowsSink = Box::new(move |event, cx| {
            pane.update(cx, |pane, cx| pane.on_rows(event, cx)).ok();
        });
        self._rows = Some(self.client.subscribe_rows(&session, None, sink, cx));
        self.session = Some(session.clone());
        self.timeline.update(cx, |timeline, cx| {
            timeline.set_session(Some(session.clone()), Vec::new(), cx)
        });
        self.composer.update(cx, |composer, cx| {
            composer.set_session(Some(session), window, cx)
        });
        self.refresh(Change::Nothing, cx);
    }

    /// Loads the page above what's loaded, when the timeline nears its top and one isn't already loading.
    pub fn load_older(&mut self, cx: &mut Context<Self>) {
        let Some(before) = self.view.oldest.filter(|_| {
            self.view.has_before && !self.loading && self.failed_before != self.view.oldest
        }) else {
            return;
        };
        self.loading = true;
        self.refresh(Change::Nothing, cx);
        self.load_page(before, false, cx);
    }

    /// Takes a rows event: a `Jump` drops what's held and loads the page again; a row updates its item and the
    /// timeline measures only that one again.
    fn on_rows(&mut self, event: RowUpdate, cx: &mut Context<Self>) {
        match event {
            RowUpdate::Jump { seq } => {
                self._tasks.clear();
                self.view = SessionView::default();
                self.held = Some(Vec::new());
                (self.loading, self.failed_before) = (false, None);
                self.timeline.update(cx, |timeline, cx| {
                    timeline.set_entries(Vec::new(), None, cx)
                });
                self.load_page(seq + 1, true, cx);
            }
            RowUpdate::Row(row) => match &mut self.held {
                Some(held) => held.push(*row),
                None => self.apply_live(&row, cx),
            },
        }
    }

    fn apply_live(&mut self, row: &Row, cx: &mut Context<Self>) {
        let change = self.view.apply(row);
        // The entries take the new item first, so the timeline can find it to scroll to.
        self.refresh(change, cx);
        if let Change::Updated(at) = change {
            self.read_detail_if_stale(at, cx);
        }
        if change == Change::Appended
            && let Some(item) = self.view.items.last()
            && matches!(item.kind, ItemKind::User { .. })
        {
            let key = item.key.clone();
            self.timeline.update(cx, |timeline, cx| {
                timeline.follow(FollowEvent::UserItem(key), cx)
            });
        }
    }

    /// Loads the page below `before`; `first` on open, when the page also says the run going now.
    fn load_page(&mut self, before: i64, first: bool, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        let args = json!({ "session": session, "before": before, "limit": PAGE_ROWS });
        let call = self.client.call(
            "rows",
            "page",
            args,
            Some(&format!("rows/page:{session}")),
            cx,
        );
        self._tasks.push(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            pane.update(cx, |pane, cx| pane.on_page(answer, before, first, cx))
                .ok();
        }));
    }

    fn on_page(
        &mut self,
        answer: Result<Value, CallError>,
        before: i64,
        first: bool,
        cx: &mut Context<Self>,
    ) {
        self.loading = false;
        let page = answer.and_then(|page| {
            serde_json::from_value::<RowPage>(page)
                .map_err(|error| CallError::Failed(error.to_string()))
        });
        self.status = match &page {
            Ok(_) | Err(CallError::Aborted) => SharedString::default(),
            Err(CallError::Unreachable { machine }) => format!("{machine} is offline").into(),
            Err(
                CallError::NotFound(message)
                | CallError::BadArgs(message)
                | CallError::Failed(message)
                | CallError::Timeout(message),
            ) => format!("Couldn't load the session: {message}").into(),
        };
        self.failed_before = page.is_err().then_some(before);
        if let Ok(page) = page {
            if first && self.known_run.is_none() {
                self.known_run = page.active_run_id;
            }
            self.view.prepend_page(page.rows, page.has_before);
        }
        if first {
            for row in self.held.take().unwrap_or_default() {
                self.view.apply(&row);
            }
        }
        self.refresh(Change::Nothing, cx);
    }

    /// Works the timeline's entries out again from the view, and tells the composer the run going now.
    fn refresh(&mut self, change: Change, cx: &mut Context<Self>) {
        let active = self.view.active_run(self.known_run.as_deref());
        let folds = Fold::all(&self.view.items, &self.view.stats, active.as_deref());
        let changed = match change {
            Change::Updated(at) => Some(self.view.items[at].key.clone()),
            _ => None,
        };
        let (items, loading) = (&self.view.items, self.loading);
        let entries = Entry::all(items, &folds, &self.open_folds, loading, active.is_some());
        self.folds = folds;
        self.timeline.update(cx, |timeline, cx| {
            let keyed = entries
                .into_iter()
                .map(|entry| (entry.key(items), entry))
                .collect();
            timeline.set_entries(keyed, changed.as_deref(), cx);
        });
        self.composer
            .update(cx, |composer, cx| composer.set_running(active, cx));
        cx.notify();
    }

    /// One frame of the reveal: steps it, and asks for the next frame only while it's still behind.
    fn reveal_frame(&mut self, _window: &mut Window, _cx: &mut Context<Self>) {
        todo!()
    }

    /// The timeline's entries, for tests.
    #[cfg(test)]
    pub fn entries(&self, cx: &App) -> Vec<Entry> {
        self.timeline.read(cx).entries()
    }
}

#[path = "pane_actions.rs"]
mod actions;
#[path = "pane_rows.rs"]
mod rows;

/// Rows a page holds, as the web view asks for.
const PAGE_ROWS: usize = 100;

impl EventEmitter<PanelEvent> for SessionPane {}

impl Focusable for SessionPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SessionPane {
    /// The chat pane's name in the saved layout (g0-shape's `CHAT`).
    fn panel_name(&self) -> &'static str {
        "Chat"
    }

    /// A permanent tab, as the layout's other panes.
    fn closable(&self, _cx: &App) -> bool {
        false
    }
}

impl Panel for SessionPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        "Chat"
    }
}

impl Render for SessionPane {
    /// The header, the timeline, the plan card, the status (why the last call failed, or "Offline, showing the saved
    /// session") and the composer, top to bottom.
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .size_full()
            .flex()
            .flex_col()
            .child(div().flex_1().min_h_0().child(self.timeline.clone()))
            .when(!self.status.is_empty(), |pane| {
                pane.child(self.status.clone())
            })
            .child(self.composer.clone())
    }
}

#[cfg(test)]
#[path = "session_pane_action_tests.rs"]
mod action_tests;
#[cfg(test)]
#[path = "session_pane_tests.rs"]
mod tests;
