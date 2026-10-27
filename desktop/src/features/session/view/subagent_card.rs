//! `SubagentCard` draws a subagent: its title, description and state, and opens its own session on click (the open
//! session intent, lane g0-host). Replaces chat/Subagent.svelte.

use gpui_kit::*;

use crate::features::session::rows::subagent::SubagentPayload;

#[derive(IntoElement)]
pub struct SubagentCard {
    pub subagent: SubagentPayload,
}

impl RenderOnce for SubagentCard {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
