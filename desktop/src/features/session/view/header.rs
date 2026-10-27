//! `Header` is the line above the timeline: the session's title, its agent and machine, and, for a helper, a link to
//! its parent session (the "open session" intent, lane g0-host). Replaces SessionHeader.svelte.

use gpui_kit::*;

#[derive(IntoElement)]
pub struct Header {
    pub title: SharedString,
    /// "Claude Code on Workbox Test", or the agent alone on this computer.
    pub place: SharedString,
    pub parent_session: Option<String>,
}

impl RenderOnce for Header {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
