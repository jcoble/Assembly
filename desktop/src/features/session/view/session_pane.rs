//! `SessionPane` owns the open session: the selected session's id (from the selected-session state lane g0-socket
//! owns), its `SessionView`, the rows subscription and the loads it started, the timeline and the composer. Opening
//! another session drops the old subscription and loads, so a stale load stops. While a session is on screen it
//! tells the backend the newest seq shown (`sessions/read`, once the base lane's session summaries land). It registers under the chat pane's
//! name through the core registry (lane g0-host, names from lane g0-shape), never by importing another feature.
//! Replaces Conversation.svelte and the spike's session/panel.rs.

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent};
use gpui_kit::*;

use super::session_action::SessionAction;
use super::timeline::Timeline;
use crate::features::session::client::session_client::{RowsEvent, SessionClient};
use crate::features::session::composer::composer_view::Composer;
use crate::features::session::model::session_view::SessionView;

pub struct SessionPane {
    client: std::rc::Rc<dyn SessionClient>,
    focus: FocusHandle,
    session: Option<String>,
    view: SessionView,
    /// The run sent from here, or the page's active run on open, until its turn row arrives.
    known_run: Option<String>,
    timeline: Entity<Timeline>,
    composer: Entity<Composer>,
    /// The streaming reply's reveal, stepped once a frame while it's behind; `None` at rest.
    reveal: Option<crate::features::session::model::reveal::Reveal>,
    /// "Loading…", or why the last call failed; empty when there's nothing to say.
    status: SharedString,
    /// The rows subscription and every load this session started; replaced on open, dropped on close. Each call keys
    /// on the session, so a load for the session left behind stops.
    _rows: Option<Subscription>,
    _tasks: Vec<Task<()>>,
}

impl SessionPane {
    pub fn new(
        _client: std::rc::Rc<dyn SessionClient>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Self {
        todo!()
    }

    /// Shows a session: subscribes to its rows first, then, on the `Jump` that says its seq, loads the page below
    /// `seq + 1`, so the page and the live rows never overlap. A session with no rows yet (a new one) is ready at
    /// once, empty, so its first message sends.
    pub fn open(&mut self, _session: String, _window: &mut Window, _cx: &mut Context<Self>) {
        todo!()
    }

    /// Loads the page above what's loaded, when the timeline nears its top and one isn't already loading.
    pub fn load_older(&mut self, _cx: &mut Context<Self>) {
        todo!()
    }

    /// Takes a rows event: a `Jump` drops what's held and loads the page again; a row updates its item and the
    /// timeline measures only that one again.
    fn on_rows(&mut self, _event: RowsEvent, _cx: &mut Context<Self>) {
        todo!()
    }

    /// One frame of the reveal: steps it, and asks for the next frame only while it's still behind.
    fn reveal_frame(&mut self, _window: &mut Window, _cx: &mut Context<Self>) {
        todo!()
    }

    /// A row's button or link: the call it makes, or the intent it opens (lane g0-host).
    fn on_action(&mut self, _action: SessionAction, _window: &mut Window, _cx: &mut Context<Self>) {
        todo!()
    }
}

impl EventEmitter<PanelEvent> for SessionPane {}

impl Focusable for SessionPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SessionPane {
    fn panel_name(&self) -> &'static str {
        todo!()
    }
}

impl Panel for SessionPane {
    /// The session's title, cut to one line.
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}

impl Render for SessionPane {
    /// The header, the timeline, the plan card, the status (why the last call failed, or "Offline, showing the saved
    /// session") and the composer, top to bottom.
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}

#[cfg(test)]
#[path = "session_pane_tests.rs"]
mod tests;
