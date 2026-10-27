//! `SessionRow` is one session in the column: its title, agent and machine, and a still dot while it works (no
//! loop, and the age column never moves). Selected, it's marked.

use gpui_kit::*;

#[derive(IntoElement)]
pub struct SessionRow {
    pub session_id: String,
    pub title: SharedString,
    /// "Claude Code · Workbox Test".
    pub place: SharedString,
    pub working: bool,
    pub selected: bool,
}

impl RenderOnce for SessionRow {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
