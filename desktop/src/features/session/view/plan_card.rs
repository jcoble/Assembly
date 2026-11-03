//! `PlanCard` is the old composer's plan chip above the box: a centred pill reading "Step 2 / 5 · 3 files changed +40 −2"
//! with a panel of steps that opens upward over the timeline, so opening it moves nothing. The pill also carries the
//! turn's file totals when the agent gave no plan. The panel shuts when the turn ends. Replaces PlanChip.svelte.

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::item_row::CHAT_COLUMN;
use super::session_action::{OnAction, SessionAction};
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::rows::plan::{PlanItem, PlanStatus};
use crate::features::session::rows::tool::ToolState;

#[derive(IntoElement)]
pub struct PlanCard {
    pub items: Vec<PlanItem>,
    pub changes: Option<FileChanges>,
    pub open: bool,
    /// Whether the session is working now. A turn stopped with a step still in progress must not keep spinning.
    pub running: bool,
    pub on_action: OnAction,
}

/// How many files the newest turn touched, and by how many lines.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FileChanges {
    pub files: usize,
    pub added: i64,
    pub removed: i64,
}

/// The step the agent is on, from 1: the one in progress, else the first pending, else the last.
pub fn current_step(items: &[PlanItem]) -> usize {
    let at = |status| items.iter().position(|item| item.status == status);
    at(PlanStatus::InProgress)
        .or_else(|| at(PlanStatus::Pending))
        .map_or(items.len(), |at| at + 1)
}

/// The totals back to the newest message the user sent, from tools that finished and said they changed a file.
pub fn turn_file_changes(items: &[Item]) -> Option<FileChanges> {
    let mut paths = std::collections::HashSet::new();
    let mut changes = FileChanges::default();
    for item in items.iter().rev() {
        match &item.kind {
            ItemKind::User { .. } => break,
            ItemKind::Tool { item_id, tool }
                if tool.state == ToolState::Completed
                    && (tool.added.is_some()
                        || tool.removed.is_some()
                        || (tool.kind.as_deref() == Some("edit") && tool.path.is_some())) =>
            {
                paths.insert(tool.path.as_deref().unwrap_or(item_id));
                changes.added += tool.added.unwrap_or(0);
                changes.removed += tool.removed.unwrap_or(0);
            }
            _ => {}
        }
    }
    changes.files = paths.len();
    (changes.files > 0).then_some(changes)
}

/// One mark for a step in the pill and the panel: an open ring waiting, the spinner while it runs, a disc once done.
fn mark(status: PlanStatus, spin: bool, cx: &App) -> AnyElement {
    let accent = cx.theme().primary;
    let disc = div().m(px(1.)).size(px(12.)).flex_none().rounded_full();
    match status {
        PlanStatus::InProgress if spin => Spinner::new()
            .with_size(px(14.))
            .color(accent)
            .into_any_element(),
        PlanStatus::InProgress => Icon::new(Glyph::CircleDot)
            .size(px(14.))
            .text_color(accent)
            .into_any_element(),
        PlanStatus::Pending => disc
            .border_1()
            .border_color(cx.theme().muted_foreground)
            .into_any_element(),
        PlanStatus::Completed => disc.bg(accent).into_any_element(),
    }
}

fn panel(items: &[PlanItem], running: bool, cx: &App) -> Div {
    let theme = cx.theme();
    let (foreground, muted) = (theme.foreground, theme.muted_foreground);
    let steps: Vec<AnyElement> = items
        .iter()
        .map(|item| {
            h_flex()
                .items_start()
                .gap(px(11.))
                .text_color(match item.status {
                    PlanStatus::InProgress => foreground,
                    PlanStatus::Pending => muted,
                    PlanStatus::Completed => muted.opacity(0.7),
                })
                .when(item.status == PlanStatus::InProgress, |step| {
                    step.font_weight(FontWeight::MEDIUM)
                })
                .child(mark(item.status, running, cx))
                .child(div().min_w_0().child(item.text.clone()))
                .into_any_element()
        })
        .collect();
    // Fills the chip's width only to centre the panel; the panel alone takes the mouse.
    div()
        .absolute()
        .left_0()
        .right_0()
        .bottom(relative(1.))
        .pb(px(10.))
        .flex()
        .justify_center()
        .child(
            v_flex()
                .occlude()
                .min_w(px(240.))
                .max_w(px(620.))
                .px(px(17.))
                .py(px(13.))
                .gap(px(11.))
                .rounded(px(12.))
                .bg(theme.popover)
                .shadow_lg()
                .text_size(px(14.))
                .children(steps),
        )
}

impl RenderOnce for PlanCard {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let on_action = self.on_action.clone();
        let has_plan = !self.items.is_empty();
        let current = self.items.get(current_step(&self.items).wrapping_sub(1));
        let step = current.map(|item| {
            (
                mark(item.status, self.running, cx),
                format!("Step {} / {}", current_step(&self.items), self.items.len()),
            )
        });
        let panel = (has_plan && self.open).then(|| panel(&self.items, self.running, cx));
        let theme = cx.theme();
        let (popover, hover, muted) = (theme.popover, theme.accent, theme.muted_foreground);
        let (success, danger) = (theme.success, theme.danger);
        let changes = self.changes.map(|changes| {
            let word = if changes.files == 1 { "file" } else { "files" };
            (changes, format!("{} {word} changed", changes.files))
        });
        div()
            .relative()
            .w_full()
            .max_w(CHAT_COLUMN)
            .mx_auto()
            .mb(px(10.))
            .flex()
            .justify_center()
            .children(panel)
            .child(
                h_flex()
                    .id("plan")
                    .min_w_0()
                    .max_w(px(560.))
                    .gap(px(8.))
                    .px(px(16.))
                    .py(px(8.))
                    .rounded_full()
                    .bg(popover)
                    .shadow_sm()
                    .text_size(px(14.))
                    .when(has_plan, |pill| {
                        pill.cursor_pointer()
                            .hover(move |pill| pill.bg(hover))
                            .on_click(move |_, window, cx| {
                                on_action(SessionAction::TogglePlan, window, cx)
                            })
                    })
                    .when_some(step, |pill, (mark, words)| {
                        pill.child(mark).child(div().flex_none().child(words))
                    })
                    .when_some(changes, |pill, (changes, words)| {
                        pill.when(has_plan, |pill| pill.child(div().text_color(muted).child("·")))
                            .child(div().min_w_0().truncate().child(words))
                            // A turn that changed files without a diff has no line counts to show.
                            .when(changes.added + changes.removed > 0, |pill| {
                                pill.child(
                                    div()
                                        .text_color(success)
                                        .child(format!("+{}", changes.added)),
                                )
                                .child(
                                    div()
                                        .text_color(danger)
                                        .child(format!("−{}", changes.removed)),
                                )
                            })
                    }),
            )
    }
}

#[cfg(test)]
#[path = "tests/plan_card_tests.rs"]
mod tests;
