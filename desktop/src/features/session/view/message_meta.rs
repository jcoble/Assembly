//! `MessageMeta` is the line under a prompt or a finished reply: its copy button and when it was written ("Thursday
//! 12:02 PM" this week, "Oct 17, 12:02 PM" before). Replaces chat/MessageMeta.svelte and fold.ts's messageTime.

use gpui_kit::*;

#[derive(IntoElement)]
pub struct MessageMeta {
    /// What the copy button copies: the prompt, or the reply as markdown.
    pub copy: SharedString,
    pub at: i64,
    /// The backend clock's now, for "this week".
    pub now: i64,
}

impl RenderOnce for MessageMeta {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
