//! `PlanCard` draws the agent's newest plan above the composer: a "Step 2 / 5" pill that opens the list of steps,
//! each with its status, the one in progress marked. It shuts when the turn ends. Replaces chat/tools/Plan.svelte.

use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::session_action::{OnAction, SessionAction};
use crate::features::session::rows::plan::{PlanItem, PlanStatus};

#[derive(IntoElement)]
pub struct PlanCard {
    pub items: Vec<PlanItem>,
    pub open: bool,
    pub on_action: OnAction,
}

/// The step the agent is on, from 1: the one in progress, else the first pending, else the last.
pub fn current_step(items: &[PlanItem]) -> usize {
    let at = |status| items.iter().position(|item| item.status == status);
    at(PlanStatus::InProgress)
        .or_else(|| at(PlanStatus::Pending))
        .map_or(items.len(), |at| at + 1)
}

fn mark(status: PlanStatus) -> &'static str {
    match status {
        PlanStatus::Pending => "○",
        PlanStatus::InProgress => "◐",
        PlanStatus::Completed => "✓",
    }
}

impl RenderOnce for PlanCard {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let step = current_step(&self.items);
        let on_action = self.on_action.clone();
        let steps = v_flex()
            .gap_1()
            .px_3()
            .py_2()
            .rounded(theme.radius)
            .border_1()
            .border_color(theme.border)
            .text_sm()
            .children(self.items.iter().map(|item| {
                h_flex()
                    .gap_2()
                    .when(item.status == PlanStatus::InProgress, |line| {
                        line.font_weight(FontWeight::MEDIUM)
                    })
                    .when(item.status == PlanStatus::Completed, |line| {
                        line.text_color(theme.muted_foreground)
                    })
                    .child(mark(item.status))
                    .child(item.text.clone())
            }));
        let status = self
            .items
            .get(step.saturating_sub(1))
            .map_or(PlanStatus::Pending, |item| item.status);
        v_flex()
            .w_full()
            .px_4()
            .gap_1()
            .items_center()
            .when(self.open, |column| column.child(steps))
            .child(
                h_flex()
                    .id("plan")
                    .gap_2()
                    .px_3()
                    .py_1()
                    .rounded_full()
                    .border_1()
                    .border_color(theme.border)
                    .text_sm()
                    .cursor_pointer()
                    .on_click(move |_, window, cx| on_action(SessionAction::TogglePlan, window, cx))
                    .child(mark(status))
                    .child(format!("Step {step} / {}", self.items.len())),
            )
    }
}

#[cfg(test)]
#[path = "plan_card_tests.rs"]
mod tests;
