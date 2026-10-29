//! `PlanCard` draws the agent's newest plan above the composer: a muted "Step 2 / 5" line with a chevron that opens the
//! list of steps under it, hanging from a rule, each with its status mark, the one in progress lit. It shuts when the
//! turn ends. Replaces chat/tools/Plan.svelte.

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::grow::chevron;
use super::item_row::CHAT_COLUMN;
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

fn mark(status: PlanStatus) -> Icon {
    Icon::new(match status {
        PlanStatus::Pending => Glyph::Circle,
        PlanStatus::InProgress => Glyph::CircleDot,
        PlanStatus::Completed => Glyph::CircleCheck,
    })
    .size(px(14.))
}

impl RenderOnce for PlanCard {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (foreground, muted) = (theme.foreground, theme.muted_foreground);
        let step = current_step(&self.items);
        let on_action = self.on_action.clone();
        let steps = v_flex()
            .ml(px(6.))
            .pl(px(16.))
            .border_l_1()
            .border_color(theme.border)
            .children(self.items.iter().map(|item| {
                h_flex()
                    .h(px(28.))
                    .gap_2()
                    .text_color(if item.status == PlanStatus::InProgress {
                        foreground
                    } else {
                        muted
                    })
                    .child(mark(item.status))
                    .child(div().min_w_0().truncate().child(item.text.clone()))
            }));
        v_flex()
            .w_full()
            .max_w(CHAT_COLUMN)
            .mx_auto()
            .px_4()
            .pb_1()
            .text_sm()
            .child(
                h_flex()
                    .id("plan")
                    .h(px(28.))
                    .gap_2()
                    .text_color(muted)
                    .cursor_pointer()
                    .on_click(move |_, window, cx| on_action(SessionAction::TogglePlan, window, cx))
                    .child(Icon::new(Glyph::ListChecks).size(px(14.)))
                    .child(format!("Step {step} / {}", self.items.len()))
                    .child(chevron(if self.open { 1. } else { 0. })),
            )
            .when(self.open, |column| column.child(steps))
    }
}

#[cfg(test)]
#[path = "plan_card_tests.rs"]
mod tests;
