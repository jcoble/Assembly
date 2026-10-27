//! `SessionPane` owns the open session: the selected session's id (from the selected-session state lane g0-socket
//! owns), its `SessionView`, the rows subscription and the loads it started, the timeline and the composer. Opening
//! another session drops the old subscription and loads, so a stale load stops. It registers under the chat pane's
//! name through the core registry (lane g0-host, names from lane g0-shape), never by importing another feature.
//! Replaces Conversation.svelte and the spike's session/panel.rs.

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent};
use gpui_kit::*;

use super::timeline::Timeline;
use crate::features::session::client::session_client::SessionClient;
use crate::features::session::composer::composer::Composer;
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
    /// "Loading…", or why the last call failed; empty when there's nothing to say.
    status: SharedString,
    /// The rows subscription and every load this session started; replaced on open, dropped on close.
    _rows: Option<Subscription>,
    _tasks: Vec<Task<()>>,
}

impl SessionPane {
    pub fn new(_client: std::rc::Rc<dyn SessionClient>, _window: &mut Window, _cx: &mut Context<Self>) -> Self {
        todo!()
    }

    /// Shows a session: its newest page, then its rows from the page's newest seq on. A session with no rows yet (a
    /// new one) is ready at once, empty, so its first message sends.
    pub fn open(&mut self, _session: String, _cx: &mut Context<Self>) {
        todo!()
    }

    /// Loads the page above what's loaded, when the timeline nears its top and one isn't already loading.
    pub fn load_older(&mut self, _cx: &mut Context<Self>) {
        todo!()
    }

    /// Applies a batch of live rows and tells the timeline which items to append or measure again.
    fn apply_rows(&mut self, _rows: Vec<crate::features::session::rows::row::Row>, _cx: &mut Context<Self>) {
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
    /// The header, the timeline, the status line and the composer, top to bottom.
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
