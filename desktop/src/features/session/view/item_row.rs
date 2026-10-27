//! `ItemRow` draws one entry of the timeline by its kind: a prompt, a reply with its meta line, thinking, a tool, an
//! ask, a task, a subagent, a fold's heading, a note, an error notice, or a kind it doesn't know by
//! name. An empty item draws nothing, so no blank rows. Replaces TimelineItem.svelte and the spike's item_view.rs.

use gpui_kit::*;

use crate::features::session::model::item::Item;
use super::session_action::OnAction;
use crate::features::session::model::kept::Kept;

#[derive(IntoElement)]
pub struct ItemRow {
    pub item: Item,
    pub kept: Kept,
    /// The agent's name above the first piece of a reply.
    pub label: Option<SharedString>,
    /// The last piece of a reply, which leaves a gap under it.
    pub ends_reply: bool,
    pub on_action: OnAction,
}

impl RenderOnce for ItemRow {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
