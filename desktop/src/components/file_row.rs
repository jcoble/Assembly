//! `file_row` is the look's file row (`.frow`): the kit's list item 32 high with the field corners, Body text in
//! text-1, and the selected outline on the chosen one. Source control's changed files, the Search tab's results and the
//! Files tab's tree use it; the caller puts the mark, name and folder inside and wires the click.

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_FIELD, FILE_ROW_HEIGHT, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::list::ListItem;
use gpui_kit::*;

/// One file row; `selected` marks the one picked, with the kit's selected fill and the look's selected outline. It
/// takes its column's width, so a long name ends in "…" rather than widening a row in a list drawn one row at a time.
pub fn file_row(id: impl Into<ElementId>, selected: bool, cx: &App) -> ListItem {
    let look = Look::of(cx);
    ListItem::new(id)
        .w_full()
        .selected(selected)
        .px(SPACE[3])
        .py_0()
        .h(FILE_ROW_HEIGHT)
        .rounded(CORNER_FIELD)
        .border_1()
        .border_color(if selected {
            look.line_selected
        } else {
            transparent_black()
        })
        .text_role(TextRole::Body, cx)
        .text_color(look.text_1)
}
