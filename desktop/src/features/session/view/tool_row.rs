//! `ToolRow` draws a tool call: its mark (running, done or failed), title, summary and line counts, and, opened, its
//! command, output tail and diff from `rows/tool`. The detail is read when it opens and again only when the call's
//! state changes (an edit's diff once the edit finishes). A long diff shows its first lines until shown in full. Opening and closing ease, as folds do.
//! Replaces chat/Tool.svelte and chat/tools/tool.ts.

use gpui_kit::*;

use super::session_action::OnAction;

use crate::features::session::model::kept::Kept;
use crate::features::session::rows::tool::ToolPayload;

/// A diff past this many lines shows its first `DIFF_SHOWN` until shown in full.
pub const DIFF_FOLD: usize = 20;
pub const DIFF_SHOWN: usize = 12;
/// The most output lines an open tool shows, from its end.
pub const OUTPUT_TAIL: usize = 200;

#[derive(IntoElement)]
pub struct ToolRow {
    pub item_id: String,
    pub tool: ToolPayload,
    pub kept: Kept,    pub on_action: OnAction,
}

impl RenderOnce for ToolRow {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
