//! A row of icon segments in a round track, the look's `.iseg`: one kit `Button` per segment, each its own capsule, 36
//! wide and 28 high, 2 apart inside a 3px inset capsule track with its line. The picked segment is marked toggled and
//! takes the selected fill and line, the others show the hover wash under the pointer. Callers: the drawer's tool
//! capsule (features/shell/drawer_buttons.rs).
//!
//! Not the kit's `ButtonGroup`: it joins its buttons' corners (button_group.rs:183-225), which squared the picked
//! segment's inner side. The group adds no role of its own, so a plain track loses nothing; each segment keeps its click.

use gpui_kit::assets::IconName;
use gpui_kit::component::Icon;
use gpui_kit::component::Selectable as _;
use gpui_kit::component::button::{Button, ButtonCustomVariant, ButtonVariants as _};
use gpui_kit::component::h_flex;
use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::CORNER_PILL;

/// The look's segment and the room around it.
const SEGMENT: Size<Pixels> = size(px(36.), px(28.));
const SEGMENT_GAP: Pixels = px(2.);
const TRACK_PADDING: Pixels = px(3.);
const SEGMENT_ICON: Pixels = px(16.);

/// The track; add the segments with `.child(segment(..))`.
pub fn icon_segments(id: impl Into<ElementId>, cx: &App) -> Stateful<Div> {
    let look = Look::of(cx);
    h_flex()
        .id(id)
        .flex_none()
        .p(TRACK_PADDING)
        .gap(SEGMENT_GAP)
        .rounded(CORNER_PILL)
        .bg(look.inset)
        .border_1()
        .border_color(look.line)
}

/// One segment, `picked` while its tool is the one shown.
pub fn segment(id: impl Into<ElementId>, icon: IconName, picked: bool, cx: &App) -> Button {
    let look = Look::of(cx);
    let (text, line) = if picked {
        (look.text_1, look.line_selected)
    } else {
        (look.text_2, transparent_black())
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
        // What the kit's group would have said: the picked segment is the pressed one.
        .toggled(picked)
        .child(Icon::new(icon).size(SEGMENT_ICON))
        .w(SEGMENT.width)
        .h(SEGMENT.height)
        .px_0()
        .rounded(CORNER_PILL)
        .border_1()
        .border_color(line)
}
