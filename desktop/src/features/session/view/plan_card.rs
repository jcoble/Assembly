//! `PlanCard` draws the agent's newest plan above the composer: each step with its status, the one in progress
//! marked; a step's checkmark appears as it completes. Replaces chat/tools/Plan.svelte.

use gpui_kit::*;

use crate::features::session::rows::plan::PlanItem;

#[derive(IntoElement)]
pub struct PlanCard {
    pub items: Vec<PlanItem>,
    pub open: bool,
}

impl RenderOnce for PlanCard {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
