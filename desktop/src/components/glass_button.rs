//! A kit `Button` on the window's glass: the top bar's controls (Home, Board, the terminal and drawer toggles,
//! "N need you"), drawn as the rule sheet's "Window chrome and top bar" draws them. Callers: top_bar.rs,
//! terminal_toggle.rs, drawer_buttons.rs.
//!
//! It uses the kit's custom button colours, so the button's resting, hover, pressed and selected fills are the
//! on-glass ones rather than the theme's opaque greys: no fill at rest, the hover wash under the pointer, and the
//! on-glass selected fill while pressed, picked, or holding its menu or popover open. Text and icons use the on-glass
//! secondary colour, or the primary one while picked.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonCustomVariant, ButtonVariants as _};
use gpui_kit::component::{Icon, Selectable as _};
use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{BUTTON_HEIGHT, CORNER_PILL};

/// An icon inside a glass button: the sheet's icon-button size.
pub const GLASS_ICON: Pixels = px(16.);

/// A pill-shaped glass button, `selected` while what it opens is showing. The caller adds its words or icon, any
/// padding, and the outline Board has.
pub fn glass_button(id: impl Into<ElementId>, selected: bool, cx: &App) -> Button {
    let look = Look::of(cx);
    let text = if selected {
        look.on_glass_text_1
    } else {
        look.on_glass_text_2
    };
    Button::new(id)
        .custom(
            ButtonCustomVariant::new(cx)
                .foreground(text)
                .hover(look.fill_hover)
                .active(look.on_glass_fill_selected),
        )
        .selected(selected)
        .rounded(CORNER_PILL)
        .h(BUTTON_HEIGHT)
}

/// A round 32px glass button holding one 16px icon.
pub fn glass_icon_button(
    id: impl Into<ElementId>,
    icon: IconName,
    selected: bool,
    cx: &App,
) -> Button {
    // The icon goes in as a child rather than through the kit's `icon`, which sizes it to three quarters of the
    // button; the square size and zero padding then replace the kit's padding for a button with content.
    glass_button(id, selected, cx)
        .child(Icon::new(icon).size(GLASS_ICON))
        .size(BUTTON_HEIGHT)
        .px_0()
}
