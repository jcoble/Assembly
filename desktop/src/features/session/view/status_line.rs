//! `StatusLine` is the line above the composer while something is going on: "Working for 12s" while a turn runs
//! (counted on the backend clock's second tick, only while it runs), why the last call failed, or "Offline, showing
//! the saved session" when its machine is unreachable. Empty at rest; nothing animates. Replaces the working line in
//! Conversation.svelte.

use gpui_kit::*;

#[derive(IntoElement)]
pub struct StatusLine {
    pub text: SharedString,
}

impl RenderOnce for StatusLine {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
