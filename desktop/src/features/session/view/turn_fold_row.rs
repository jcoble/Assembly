//! `TurnFoldRow` is a finished turn's heading: "Worked for 42s · ran 3 commands · edited 2 files", muted, with the
//! chevron after it, opening and closing the work it folds. Replaces chat/TurnFold.svelte.

use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::*;

use std::time::Instant;

use super::grow::{chevron, turn};
use super::session_action::{OnAction, SessionAction};
use crate::features::session::model::fold::Fold;
use crate::features::session::model::turn_line::worked_for;

#[derive(IntoElement)]
pub struct TurnFoldRow {
    pub fold: Fold,
    pub open: bool,
    /// When it was last opened or shut: its chevron turns from then.
    pub toggled_at: Option<Instant>,
    pub on_action: OnAction,
}

/// "Worked for 42s · edited 2 files", or either half alone; the summary leading starts with a capital.
pub fn heading(fold: &Fold) -> String {
    let worked = fold
        .duration_ms
        .map(|ms| format!("Worked for {}", worked_for(ms)));
    let summary = Some(fold.summary.clone()).filter(|summary| !summary.is_empty());
    match (worked, summary) {
        (Some(worked), Some(summary)) => format!("{worked} · {summary}"),
        (Some(text), None) => text,
        (None, Some(summary)) => {
            let mut chars = summary.chars();
            (chars.next().into_iter())
                .flat_map(char::to_uppercase)
                .chain(chars)
                .collect()
        }
        (None, None) => "Worked".to_string(),
    }
}

impl RenderOnce for TurnFoldRow {
    fn render(self, window: &mut Window, cx: &mut App) -> impl IntoElement {
        let run_id = self.fold.run_id.clone();
        let chevron = chevron(turn(self.open, self.toggled_at, window, cx).0);
        let on_action = self.on_action.clone();
        h_flex()
            .id(SharedString::from(format!("fold:{run_id}")))
            .h(px(22.))
            .gap_2()
            .text_size(px(13.))
            .text_color(cx.theme().muted_foreground)
            .cursor_pointer()
            .on_click(move |_, window, cx| {
                let run_id = run_id.clone();
                on_action(SessionAction::ToggleFold { run_id }, window, cx)
            })
            .child(heading(&self.fold))
            .child(chevron)
    }
}

#[cfg(test)]
#[path = "turn_fold_row_tests.rs"]
mod tests;
