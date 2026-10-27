//! `SessionsPane` draws the sessions list as a virtual list, loading the next page (`sessions/page`) as the reader
//! nears its end. It registers under the sessions pane's name through the core registry (lane g0-host).

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent};
use gpui_kit::*;

pub struct SessionsPane {
    focus: FocusHandle,
    /// The new-session form, while it's open.
    creating: bool,
}

impl SessionsPane {
    pub fn new(_cx: &mut Context<Self>) -> Self {
        todo!()
    }
}

impl EventEmitter<PanelEvent> for SessionsPane {}

impl Focusable for SessionsPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SessionsPane {
    fn panel_name(&self) -> &'static str {
        todo!()
    }

    /// The sessions column is a permanent pane.
    fn closable(&self, _cx: &App) -> bool {
        false
    }
}

impl Panel for SessionsPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        "Sessions"
    }
}

impl Render for SessionsPane {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
