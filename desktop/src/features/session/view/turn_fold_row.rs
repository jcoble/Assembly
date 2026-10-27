//! `TurnFoldRow` is a finished turn's heading: "Worked for 42s · Edited 2 files, ran 3 commands", opening and closing
//! the work it folds, with its chevron. Replaces chat/TurnFold.svelte.

use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::*;

use super::session_action::{OnAction, SessionAction};
use crate::features::session::model::fold::Fold;
use crate::features::session::model::turn_line::worked_for;

#[derive(IntoElement)]
pub struct TurnFoldRow {
    pub fold: Fold,
    pub open: bool,
    pub on_action: OnAction,
}

/// "Worked for 42s · Edited 2 files", or either half alone.
pub fn heading(fold: &Fold) -> String {
    let worked = fold
        .duration_ms
        .map(|ms| format!("Worked for {}", worked_for(ms)));
    let summary = Some(fold.summary.clone()).filter(|summary| !summary.is_empty());
    match (worked, summary) {
        (Some(worked), Some(summary)) => format!("{worked} · {summary}"),
        (Some(text), None) | (None, Some(text)) => text,
        (None, None) => "Worked".to_string(),
    }
}

impl RenderOnce for TurnFoldRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let run_id = self.fold.run_id.clone();
        let on_action = self.on_action.clone();
        h_flex()
            .id(SharedString::from(format!("fold:{run_id}")))
            .gap_2()
            .text_sm()
            .text_color(cx.theme().muted_foreground)
            .cursor_pointer()
            .on_click(move |_, window, cx| {
                let run_id = run_id.clone();
                on_action(SessionAction::ToggleFold { run_id }, window, cx)
            })
            .child(if self.open { "▾" } else { "▸" })
            .child(heading(&self.fold))
    }
}
