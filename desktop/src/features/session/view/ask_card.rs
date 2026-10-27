//! `AskCard` draws a permission or a question the agent is waiting on: the agent's options as buttons, or the form's
//! fields, answered through `send/answer`. An answered or cancelled one shows its answer, folded with the turn's work.
//! Replaces chat/Ask.svelte.

use gpui_kit::*;

use super::session_action::OnAction;

use crate::features::session::rows::ask::AskPayload;

#[derive(IntoElement)]
pub struct AskCard {
    pub ask_id: String,
    pub ask: AskPayload,    pub on_action: OnAction,
}

impl RenderOnce for AskCard {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
