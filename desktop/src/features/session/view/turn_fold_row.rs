//! `TurnFoldRow` is a finished turn's heading: "Worked for 42s · Edited 2 files, ran 3 commands", opening and closing
//! the work it folds, with its chevron and the work easing open and shut. A stopped turn ends with "Stopped", once. Replaces chat/TurnFold.svelte.

use gpui_kit::*;

use super::session_action::OnAction;

use crate::features::session::model::fold::Fold;

#[derive(IntoElement)]
pub struct TurnFoldRow {
    pub fold: Fold,
    pub open: bool,    pub on_action: OnAction,
}

impl RenderOnce for TurnFoldRow {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
