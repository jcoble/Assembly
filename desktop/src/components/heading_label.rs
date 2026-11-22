//! `heading_label` is the look's group heading words (`.dsec`): the name in Meta at 600 in text-2, then its count in
//! text-3. Source control's sections and commits and the Search tab's result groups use it.

use crate::core::theme::look::Look;
use crate::core::theme::metrics::SPACE;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::h_flex;
use gpui_kit::*;

/// The name and count, for a heading that doesn't fold as well as one that does.
pub fn heading_label(title: &'static str, count: Option<String>, cx: &App) -> Div {
    let look = Look::of(cx);
    h_flex()
        .gap(SPACE[2])
        .text_role(TextRole::Meta, cx)
        .font_weight(FontWeight::SEMIBOLD)
        .text_color(look.text_2)
        .child(title)
        .children(count.map(|count| {
            div()
                .font_weight(FontWeight::MEDIUM)
                .text_color(look.text_3)
                .child(count)
        }))
}
