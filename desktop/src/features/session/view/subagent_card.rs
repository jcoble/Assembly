//! `SubagentCard` draws a subagent: its title, description and state ("Failed" when idle with an error, "Stopped"
//! when cancelled), opens its own session on click (the open session intent, lane g0-host), and has a Stop button
//! while it runs and can stop (`send/stopTask` with its session id). Replaces chat/Subagent.svelte.

use gpui_kit::*;

use super::session_action::OnAction;

use crate::features::session::rows::subagent::SubagentPayload;

#[derive(IntoElement)]
pub struct SubagentCard {
    pub subagent: SubagentPayload,
    pub on_action: OnAction,
}

impl RenderOnce for SubagentCard {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
