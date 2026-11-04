//! `SessionPane` owns the open session: the selected session's id (from the selected-session state lane g0-socket
//! owns), its `SessionView`, the rows subscription and the loads it started, the timeline and the composer. Opening
//! another session drops the old subscription and loads, so a stale load stops. While a session is on screen it
//! tells the backend the newest seq shown (`sessions/read`, pane_read.rs). It registers under the chat pane's
//! name through the core registry (lane g0-host, names from lane g0-shape), never by importing another feature.
//! Replaces Conversation.svelte and the spike's session/panel.rs.

use gpui_kit::base::motion::{Presence, Transition};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use std::collections::{HashMap, HashSet};
use std::rc::Rc;
use std::time::{Duration, Instant};

use super::grow::{grow, turn};
use super::header::Header;
use super::session_action::OnAction;
use super::timeline::{RenderRow, Timeline, TimelineEvent};
use crate::core::state::machines::GlobalMachines;
use crate::core::theme::grow::{GROW, faded};
use crate::core::ui::ask::form::AskForm;
use crate::core::ui::expand_row::ExpandRow;
use crate::features::session::client::session_client::{RowUpdate, RowsSink, SessionClient};
use crate::features::session::composer::composer_view::{Composer, ComposerEvent};
use crate::features::session::composer::shape::pending_answer;
use crate::features::session::model::change::Change;
use crate::features::session::model::entries::{Entry, entry_key};
use crate::features::session::model::fold::Fold;
use crate::features::session::model::follow::FollowEvent;
use crate::features::session::model::item::ItemKind;
use crate::features::session::model::kept::Kept;
use crate::features::session::model::reveal::Reveal;
use crate::features::session::model::session_view::{SessionView, key_of};
use crate::features::session::model::turn_line::latest_branch;
use crate::features::session::rows::row::Row;
use crate::features::session::rows::tool::ToolState;

pub struct SessionPane {
    client: Rc<dyn SessionClient>,
    open_serial: u64,
    pending_open: Option<opening::PendingOpen>,
    active_open_serial: Option<u64>,
    active_reopen: Option<(
        crate::core::intents::session_open::SessionOpenRequest,
        AnyWindowHandle,
    )>,
    reopen_queued: Option<u64>,
    read_only: bool,
    active_provider: Option<String>,
    _source_connections: Vec<Subscription>,
    active_source_cut: Option<opening::ConnectionCut>,
    focus: FocusHandle,
    draft: Option<AnyView>,
    session: Option<String>,
    row_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    view: SessionView,
    /// The real run from a pushed user row, or the page's active run on open, until its turn row arrives.
    known_run: Option<String>,
    /// Each finished run's fold, by run id, as of the last refresh.
    folds: HashMap<String, Fold>,
    /// The folds the reader opened, by run id.
    open_folds: HashSet<String>,
    /// The plan's list of steps is open; it shuts when the turn ends.
    plan_open: bool,
    /// When each row that arrived live since the session opened came in, by item key, so it fades in once.
    fresh: HashMap<String, Instant>,
    /// Each open question's form, by ask id, made when its card is first drawn.
    forms: HashMap<String, AskForm>,
    /// What each item keeps while off screen, by item key.
    kept: HashMap<String, Kept>,
    /// What each row's buttons call: this pane's `on_action`.
    on_action: OnAction,
    header: Entity<Header>,
    timeline: Entity<Timeline>,
    composer: Entity<Composer>,
    /// The streaming reply's reveal, stepped once a frame while it's behind; `None` at rest.
    reveal: Option<Reveal>,
    /// When the reveal last stepped.
    revealed_at: Instant,
    /// A frame is asked for the reveal's next step.
    frame_asked: bool,
    /// The backend clock's last second while a turn runs, which the "Working for" line counts by.
    now: Option<i64>,
    /// The run going now and when it started: its turn row's time, or when the pane first saw it, as the web does,
    /// when that row isn't loaded.
    live_since: Option<(String, i64)>,
    /// Tool calls whose detail is being read, by item key, with the state it's read for.
    reading: HashMap<String, ToolState>,
    /// Each tool call, thinking and question's row that opens in place, by item key, and what the pane hears from it.
    expands: HashMap<String, (Entity<ExpandRow>, Subscription)>,
    /// The questions last drawn open, by item key: one answered since shuts the next time it's drawn.
    open_asks: HashSet<String>,
    /// The folds growing open or shut (`fold:<run>`), and whether their next frame is asked for.
    growing: HashSet<String>,
    grow_asked: bool,
    /// The clock's ticks, only while a turn runs.
    _clock: Option<Subscription>,
    /// Live rows that came before the first page, applied once it lands: a delta needs the blocks the page has.
    /// `None` once the first page is in.
    held: Option<Vec<Row>>,
    /// An older page is loading.
    loading: bool,
    /// The `before` of the last older page that failed: it isn't asked for again by itself, so a failure can't loop.
    failed_before: Option<i64>,
    /// The `before` of a first page that failed, which Try again loads.
    retry_first: Option<i64>,
    /// Why the last load failed; empty when there's nothing to say.
    status: SharedString,
    /// The rows subscription and every load this session started; replaced on open, dropped on close. Each call keys
    /// on the session, so a load for the session left behind stops.
    _rows: Option<Subscription>,
    _page: Option<Task<()>>,
    /// The reads a row's buttons started (a tool's detail, an image), by what they're for, so a newer one of the same
    /// kind replaces the older; they drop with the session.
    _loads: HashMap<String, Task<()>>,
    /// The writes they started (an answer, a Stop), kept the same way; these go on when the reader switches away.
    _writes: HashMap<String, Task<()>>,
    /// The newest seq told to the backend as read, for the open session.
    read_to: i64,
    /// The selected session, followed once the pane is registered.
    _selected: Option<Subscription>,
    /// Hears the machines list start, then follows it, for the open session's machine (pane_machine.rs).
    _machines_started: Subscription,
    _machines: Option<Subscription>,
    /// The composer's events and the timeline's scrolling.
    _heard: [Subscription; 3],
}

impl SessionPane {
    /// Shows a session: subscribes to its rows first, then, on the `Jump` that says its seq, loads the page below
    /// `seq + 1`, so the page and the live rows never overlap. A session with no rows yet (a new one) is ready at
    /// once, empty, so its first message sends.
    #[cfg(test)]
    pub(super) fn open(&mut self, session: String, window: &mut Window, cx: &mut Context<Self>) {
        if self.session.as_ref() == Some(&session) {
            return;
        }
        // The old subscription and loads drop first, so nothing of the session left behind lands here. Its writes (an
        // answer, a Stop) go on.
        self._rows = None;
        self._page = None;
        self._loads.clear();
        self.view = SessionView::default();
        self.row_source = None;
        self.read_to = 0;
        self.held = Some(Vec::new());
        self.open_folds.clear();
        self.kept.clear();
        self.fresh.clear();
        self.forms.clear();
        self.reading.clear();
        self.expands.clear();
        self.open_asks.clear();
        self.growing.clear();
        self.reveal = None;
        self.plan_open = false;
        (
            self.known_run,
            self.loading,
            self.failed_before,
            self.retry_first,
            self.status,
        ) = (None, false, None, None, SharedString::default());
        let pane = cx.weak_entity();
        let sink: RowsSink = Box::new(move |event, cx| {
            pane.update(cx, |pane, cx| pane.on_rows(event, cx)).ok();
        });
        self._rows =
            Some(
                self.client
                    .subscribe_rows(&session, None, self.row_source.as_ref(), sink, cx),
            );
        self.session = Some(session.clone());
        self.header
            .update(cx, |header, cx| header.show(Some(session.clone()), cx));
        self.timeline.update(cx, |timeline, cx| {
            timeline.set_session(Some(session.clone()), Vec::new(), cx)
        });
        self.composer.update(cx, |composer, cx| {
            composer.set_session(Some(session), window, cx)
        });
        self.tell_offline(cx);
        self.refresh(Change::Nothing, cx);
    }

    /// Takes a rows event: a `Jump` drops what's held and loads the page again; a row updates its item and the
    /// timeline measures only that one again.
    fn on_rows(&mut self, event: RowUpdate, cx: &mut Context<Self>) {
        match event {
            RowUpdate::Refused { code: _, message } => {
                self.composer
                    .update(cx, |composer, cx| composer.invalidate_source(cx));
                self._page = None;
                self._rows = None;
                self.held = None;
                self._loads.clear();
                self.loading = false;
                self.status = message.into();
                cx.notify();
            }
            RowUpdate::Jump { seq } => {
                if !self.read_only {
                    self.composer
                        .update(cx, |composer, cx| composer.recover_pending(cx));
                }
                self._page = None;
                self.view = SessionView::default();
                self.read_to = 0;
                self.held = Some(Vec::new());
                self.reveal = None;
                self.reading.clear();
                (self.loading, self.failed_before, self.retry_first) = (false, None, None);
                self.timeline.update(cx, |timeline, cx| {
                    timeline.set_entries(Vec::new(), None, cx)
                });
                self.load_page(seq + 1, true, cx);
                self.refresh(Change::Nothing, cx);
            }
            RowUpdate::Row(row) => match &mut self.held {
                Some(held) => held.push(*row),
                None => self.apply_live(&row, cx),
            },
        }
    }

    fn apply_live(&mut self, row: &Row, cx: &mut Context<Self>) {
        self.observe_prompt_row(row);
        // What the row's reply showed before it, so the reveal goes on from there.
        let key = key_of(row);
        let before = self
            .view
            .items
            .iter()
            .rev()
            .find_map(|item| match &item.kind {
                ItemKind::Message { blocks, .. } if item.key == key => Some(Reveal::chars(blocks)),
                _ => None,
            });
        let change = self.view.apply(row);
        if let (Change::Appended, Some(item)) = (change, self.view.items.last()) {
            let now = Instant::now();
            self.fresh
                .retain(|_, at| now.duration_since(*at) < FADE_FOR);
            self.fresh.insert(item.key.clone(), now);
        }
        if let Change::Appended | Change::Updated(_) = change {
            self.reveal_from(change, before);
        }
        match change {
            // A reply, tool or card changing in place in a run that isn't folded changes no entry: only its row
            // draws again, so a streaming reply never rebuilds the timeline.
            Change::Updated(at)
                if self.view.items[at]
                    .run_id
                    .as_ref()
                    .is_none_or(|run| !self.folds.contains_key(run)) =>
            {
                self.redraw(at, cx)
            }
            // The entries take the new item first, so the timeline can find it to scroll to.
            _ => self.refresh(change, cx),
        }
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

    /// Draws the item at `at` again, measured again, with the entries as they are.
    fn redraw(&mut self, at: usize, cx: &mut Context<Self>) {
        let key = entry_key(&self.view.items[at], self.live_run());
        self.timeline
            .update(cx, |timeline, cx| timeline.redraw_row(&key, cx));
    }

    /// The run going now, as the entries were last worked out.
    fn live_run(&self) -> Option<&str> {
        self.live_since.as_ref().map(|(run, _)| run.as_str())
    }

    /// Works the timeline's entries out again from the view, and tells the composer the run going now and the newest
    /// context report.
    fn refresh(&mut self, change: Change, cx: &mut Context<Self>) {
        let pending_changed = self.sync_pending(cx);
        let active = self.view.active_run(self.known_run.as_deref());
        let folds = Fold::all(&self.view.items, &self.view.stats, active.as_deref());
        // An item changed in place is measured again, and a running turn's card on any change: a step can arrive in
        // it live or on an older page.
        let changed = match change {
            Change::Updated(at) => Some(entry_key(&self.view.items[at], active.as_deref())),
            _ => None,
        }
        .or_else(|| active.is_some().then(|| Entry::Working.key(&[])));
        let (items, loading) = (&self.view.items, self.loading);
        let entries = Entry::all(items, &folds, &self.open_folds, loading, active.as_deref());
        self.folds = folds;
        self.timeline.update(cx, |timeline, cx| {
            let keyed = entries
                .into_iter()
                .map(|entry| (entry.key(items), entry))
                .collect();
            timeline.set_entries(keyed, changed.as_deref(), cx);
            for key in pending_changed {
                timeline.redraw_row(&key, cx);
            }
        });
        // The turn row's time once it's loaded (the backend's clock, as the steps' are); until then when the pane first
        // saw the run, on the backend clock if it's ticking.
        let started = active
            .as_deref()
            .and_then(|run| self.view.run_started_at(run));
        if self.live_since.as_ref().map(|(run, _)| run) != active.as_ref() {
            self.live_since = active.clone().map(|run| {
                let seen = self
                    .now
                    .unwrap_or_else(|| chrono::Utc::now().timestamp_millis());
                (run, started.unwrap_or(seen))
            });
        } else if let (Some((_, since)), Some(started)) = (&mut self.live_since, started) {
            *since = started;
        }
        match (active.is_some(), self._clock.is_some()) {
            (true, false) => {
                let pane = cx.weak_entity();
                let sink = Box::new(move |now, cx: &mut App| {
                    pane.update(cx, |pane, cx| {
                        pane.now = Some(now);
                        pane.timeline.update(cx, |_, cx| cx.notify());
                    })
                    .ok();
                });
                self._clock = Some(self.client.clock(sink, cx));
            }
            (false, true) => (self._clock, self.now, self.plan_open) = (None, None, false),
            _ => {}
        }
        let context = self.view.context;
        let running = active.is_some();
        let branch = latest_branch(&self.view.stats);
        let lock = pending_answer(&self.view.items);
        self.composer.update(cx, |composer, cx| {
            composer.set_running(active, cx);
            composer.set_context(context, cx);
            composer.set_branch(branch, cx);
            composer.set_locked(lock, cx);
        });
        self.tell_read(running, cx);
        cx.notify();
    }

    /// The key of a row that arrived live moments ago, which fades in as it's drawn.
    fn fresh_key(&self, entry: &Entry) -> Option<String> {
        let Entry::Item(at) = entry else { return None };
        let key = &self.view.items.get(*at)?.key;
        let at = self.fresh.get(key)?;
        (at.elapsed() < FADE_FOR).then(|| key.clone())
    }

    /// The timeline's entries, for tests.
    #[cfg(test)]
    pub fn entries(&self, cx: &App) -> Vec<Entry> {
        self.timeline.read(cx).entries()
    }
}

#[path = "pane_actions.rs"]
mod actions;
#[path = "pane_construct.rs"]
mod construct;
#[path = "pane_expand.rs"]
mod expand;
#[path = "pane_machine.rs"]
mod machine;
#[path = "pane_pages.rs"]
mod pages;
#[path = "pane_read.rs"]
mod read;
#[path = "pane_render.rs"]
mod render;
#[path = "pane_reveal.rs"]
mod reveal;
#[path = "pane_rows.rs"]
mod rows;

/// How long after it arrives a live row still fades in when it's drawn; drawn later, after scrolling back, it doesn't.
const FADE_FOR: Duration = Duration::from_millis(400);

/// A row fading in, on the theme's timing; plain with Reduce Motion on.
fn fade_in(key: String, row: AnyElement, window: &mut Window, cx: &mut App) -> AnyElement {
    let shown = Presence::new(SharedString::from(format!("{key}:fade")), true)
        .transition(Transition::new(GROW))
        .sample(window, cx);
    div().opacity(shown.progress).child(row).into_any_element()
}

/// Rows a page holds, as the web view asks for.
const PAGE_ROWS: usize = 100;

#[cfg(test)]
#[path = "tests/session_pane_action_tests.rs"]
mod action_tests;
#[cfg(test)]
#[path = "tests/session_pane_activity_tests.rs"]
mod activity_tests;
#[cfg(test)]
#[path = "tests/session_pane_real_switch_tests.rs"]
mod real_switch_tests;
#[cfg(test)]
#[path = "tests/session_pane_reveal_tests.rs"]
mod reveal_tests;
#[cfg(test)]
#[path = "tests/session_pane_switch_tests.rs"]
mod switch_tests;
#[cfg(test)]
#[path = "tests/session_pane_tests.rs"]
mod tests;
#[cfg(test)]
#[path = "tests/session_pane_write_tests.rs"]
mod write_tests;

#[cfg(test)]
#[path = "tests/session_open_native_tests.rs"]
mod open_native_tests;
#[cfg(test)]
#[path = "tests/session_open_refusal_tests.rs"]
mod open_refusal_tests;
#[cfg(test)]
#[path = "tests/session_open_tests.rs"]
mod open_tests;
#[path = "navigation/pane_open.rs"]
mod opening;
#[path = "navigation/pane_open_read.rs"]
mod opening_read;

#[path = "navigation/pane_workspace.rs"]
mod workspace;

#[path = "navigation/pane_discovery.rs"]
mod discovery;
#[cfg(test)]
#[path = "tests/session_route_tests.rs"]
mod route_tests;

#[path = "navigation/chat_codec.rs"]
pub(super) mod chat_codec;
#[cfg(test)]
#[path = "navigation/chat_codec_tests.rs"]
mod chat_codec_tests;

#[path = "navigation/helper_stop.rs"]
mod helper_stop;
#[cfg(test)]
#[path = "tests/helper_stop_tests.rs"]
mod helper_stop_tests;

#[cfg(test)]
#[path = "navigation/navigation_switch_tests.rs"]
mod navigation_switch_tests;
#[cfg(test)]
#[path = "navigation/test_navigation.rs"]
pub(crate) mod test_navigation;

#[path = "navigation/pane_reconnect.rs"]
mod reconnect;
#[cfg(test)]
#[path = "navigation/reconnect_tests.rs"]
mod reconnect_tests;
