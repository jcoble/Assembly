//! `search_field` is the look's search box at the top of a list column: the kit's small field, inset in the tile with
//! its outline, and a text-3 magnifier before the words. The Sessions and Search tabs use it.

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_FIELD, FIELD_HEIGHT, SPACE};
use gpui_kit::assets::IconName;
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::{Icon, Sizable as _};
use gpui_kit::*;

/// The magnifier before the words.
const ICON_SIZE: Pixels = px(16.);
/// Between the magnifier and the words.
const ICON_GAP: Pixels = px(9.);

/// The field over `state`, taking the room its row leaves it.
pub fn search_field(state: &Entity<InputState>, cx: &App) -> Input {
    let look = Look::of(cx);
    Input::new(state)
        .small()
        .prefix(
            Icon::new(IconName::Search)
                .size(ICON_SIZE)
                .text_color(look.text_3),
        )
        .flex_1()
        .min_w_0()
        .h(FIELD_HEIGHT)
        .px(SPACE[5])
        .gap(ICON_GAP)
        .rounded(CORNER_FIELD)
        .bg(look.inset)
        .border_color(look.line)
}
