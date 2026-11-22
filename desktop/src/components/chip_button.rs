//! A kit `Button` drawn as the look's chip (the mockup's `.chip`): round, outlined and unfilled, in text-2 at rest,
//! with the hover wash under the pointer; when picked it takes the selected fill and line in text-1, as a selected
//! row is drawn. Callers: the filter chips (core/ui/filter_pills.rs) and a project's "+ New" (project_header.rs).
//!
//! The kit's button sets its words' size itself, so a caller puts its words in a child that carries the text role.

use gpui_kit::component::Selectable as _;
use gpui_kit::component::button::{Button, ButtonCustomVariant, ButtonVariants as _};
use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CHIP_HEIGHT, CORNER_PILL, SPACE};

/// A 28px chip, `picked` while its filter or value is on. The caller adds its words and any icon.
pub fn chip_button(id: impl Into<ElementId>, picked: bool, cx: &App) -> Button {
    let look = Look::of(cx);
    let (line, text) = if picked {
        (look.line_selected, look.text_1)
    } else {
        (look.line_chip, look.text_2)
    };
    // The kit's custom colours: no fill at rest, the hover wash, and the selected fill while pressed or picked.
    Button::new(id)
        .custom(
            ButtonCustomVariant::new(cx)
                .foreground(text)
                .hover(look.fill_hover)
                .active(look.fill_selected),
        )
        .selected(picked)
        .rounded(CORNER_PILL)
        .h(CHIP_HEIGHT)
        .px(SPACE[5])
        .border_1()
        .border_color(line)
}
