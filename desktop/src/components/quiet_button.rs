//! The quiet kit `Button`s a tile uses: no fill at rest, the kit's ghost hover, round, 32 high. `quiet_button` is a
//! worded one in text-2 (the sessions footer's Settings, Resources and Usage); `quiet_icon_button` is the rule sheet's
//! 32 by 32 icon button with a 16px icon in text-3 (the chat crumb's sessions toggle, the drawer head's more-panels
//! and close). Callers add their name, tooltip and click.

use gpui_kit::assets::IconName;
use gpui_kit::component::Icon;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{BUTTON_HEIGHT, CORNER_PILL, SPACE};

/// An icon button's icon, as the rule sheet sizes it.
const ICON_SIZE: Pixels = px(16.);

/// A worded quiet button: `icon` then `label`, in text-2.
pub fn quiet_button(
    id: impl Into<ElementId>,
    icon: IconName,
    label: &'static str,
    cx: &App,
) -> Button {
    Button::new(id)
        .ghost()
        .h(BUTTON_HEIGHT)
        .px(SPACE[4])
        .rounded(CORNER_PILL)
        .text_color(Look::of(cx).text_2)
        .icon(Icon::new(icon).size(ICON_SIZE))
        .label(label)
}

/// A 32px icon-only quiet button in text-3.
pub fn quiet_icon_button(id: impl Into<ElementId>, icon: IconName, cx: &App) -> Button {
    Button::new(id)
        .ghost()
        .size(BUTTON_HEIGHT)
        .px_0()
        .rounded(CORNER_PILL)
        .text_color(Look::of(cx).text_3)
        .child(Icon::new(icon).size(ICON_SIZE))
}
