//! The one section heading in Source control, the look's `.dsec`: the name in text-2 at 600 with its count in text-3, a
//! chevron when it folds, and the section's actions at the right edge. Called by the change sections, the commits and
//! the pull requests.
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Named as _;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::tag::Tag;
use gpui_kit::component::{Disableable as _, Icon, Sizable as _, h_flex};
use gpui_kit::*;
use std::time::Duration;

use crate::components::heading_label::heading_label;
use crate::core::theme::look::Look;
use crate::core::theme::metrics::GROUP_HEIGHT;

/// The name and count, for a heading that doesn't fold as well as one that does.
/// A count beside a heading or the branch: body-sized text in a pill no taller than its row.
pub fn count_chip(count: impl Into<SharedString>, cx: &App) -> impl IntoElement {
    Tag::secondary()
        .small()
        .h(CHIP_HEIGHT)
        .text_role(TextRole::Body, cx)
        .rounded_full()
        .child(count.into())
}
pub const CHIP_HEIGHT: Pixels = px(20.);
pub fn section_heading(
    id: impl Into<SharedString>,
    title: &'static str,
    count: Option<String>,
    open: bool,
    on_toggle: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    actions: Vec<AnyElement>,
    cx: &App,
) -> Div {
    let id: SharedString = id.into();
    // A quarter turn over 120 ms, starting again each time it folds or opens, and then still.
    let (from, to) = if open {
        (0_f32, 90_f32)
    } else {
        (90_f32, 0_f32)
    };
    let chevron = Icon::new(IconName::ChevronRight)
        .size(px(14.))
        .with_animation(
            ElementId::Name(format!("{id}-chevron-{open}").into()),
            Animation::new(Duration::from_millis(120)),
            move |icon, delta| icon.rotate(radians((from + (to - from) * delta).to_radians())),
        );
    h_flex()
        .w_full()
        .h(GROUP_HEIGHT)
        .gap_1()
        .child(
            Button::new(ElementId::Name(id))
                .named(title)
                .ghost()
                .compact()
                .on_click(on_toggle)
                .child(
                    h_flex()
                        .gap_1()
                        .text_color(Look::of(cx).text_3)
                        .child(chevron)
                        .child(heading_label(title, count, cx)),
                ),
        )
        .child(div().flex_1())
        .children(actions)
}

/// A section's worded action at its right edge ("Stage all", "Unstage all"), the look's `.lk2`: a kit ghost button in
/// text-2 at Meta 500.
pub fn text_action(
    id: SharedString,
    text: &'static str,
    disabled: bool,
    on_click: impl Fn(&mut Window, &mut App) + 'static,
    cx: &App,
) -> Button {
    Button::new(id)
        .named(text)
        .ghost()
        .compact()
        .disabled(disabled)
        .text_color(Look::of(cx).text_2)
        .child(
            div()
                .text_role(TextRole::Meta, cx)
                .font_weight(FontWeight::MEDIUM)
                .child(text),
        )
        .on_click(move |_, window, cx| {
            cx.stop_propagation();
            on_click(window, cx)
        })
}
