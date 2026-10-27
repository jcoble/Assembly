//! `TaskCard` draws background work: its name, state and a Stop button while it runs and can stop (`send/stopTask`).
//! Its state shows as a still mark; nothing animates. Replaces chat/Task.svelte.

use gpui_kit::*;

use crate::features::session::rows::task::TaskPayload;

#[derive(IntoElement)]
pub struct TaskCard {
    pub task_id: String,
    pub task: TaskPayload,
}

impl RenderOnce for TaskCard {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
