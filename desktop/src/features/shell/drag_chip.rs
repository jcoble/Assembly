//! What follows the pointer while a pane's tab is dragged, from the top bar (top_tabs.rs) or the sessions column's
//! segmented control (side_tabs.rs): the pane's name on a pill in the popover surface, lifted over whatever it
//! passes.

use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_PILL, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};

/// The dragged pane's name.
pub(super) struct DragChip(pub &'static str);

impl Render for DragChip {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let look = Look::of(cx);
        div()
            .size_full()
            .px(SPACE[5])
            .flex()
            .items_center()
            .rounded(CORNER_PILL)
            .border_1()
            .border_color(look.popover_edge)
            .bg(look.popover)
            .text_color(look.text_1)
            .text_role(TextRole::Body, cx)
            .child(self.0)
    }
}
