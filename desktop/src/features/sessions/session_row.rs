//! `SessionRow` is one session in the column: its title, agent and machine. Selected, it's marked. The still dot
//! for a working session waits for the sessions list to carry that state (the base lane's session summaries).

use gpui_kit::*;

#[derive(IntoElement)]
pub struct SessionRow {
    pub session_id: String,
    pub title: SharedString,
    /// "Claude Code · Workbox Test".
    pub place: SharedString,
    pub selected: bool,
}

impl RenderOnce for SessionRow {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
