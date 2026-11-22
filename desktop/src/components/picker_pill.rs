//! A picker's button, the look's `.pill` (rule sheet: chips and pickers): a kit ghost `Button` 30 high and round, on
//! the picker fill under its outline, in text-2, with the kit's dropdown caret. Callers: the composer's permission,
//! model and agent-settings pickers (composer/capsule/config_pills.rs), which add the words and the menu.

use gpui_kit::component::Sizable as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::CORNER_PILL;

/// The pill's height and its sides, the caret side the narrower.
const PICKER_HEIGHT: Pixels = px(30.);
const PICKER_PADDING_LEFT: Pixels = px(11.);
const PICKER_PADDING_RIGHT: Pixels = px(9.);

pub fn picker_pill(id: impl Into<ElementId>, cx: &App) -> Button {
    let look = Look::of(cx);
    Button::new(id)
        .ghost()
        .small()
        .h(PICKER_HEIGHT)
        .pl(PICKER_PADDING_LEFT)
        .pr(PICKER_PADDING_RIGHT)
        .rounded(CORNER_PILL)
        .bg(look.picker_pill)
        .border_1()
        .border_color(look.picker_pill_border)
        .text_color(look.text_2)
        .dropdown_caret(true)
}
