//! What an empty panel shows: one icon, a short title, a sentence or two, and at most one thing to press.

use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, Icon, v_flex};
use gpui_kit::*;

use crate::core::theme::text::{TextRole, TextRoleExt as _};

pub fn empty_state(
    icon: IconName,
    title: impl Into<SharedString>,
    body: impl Into<SharedString>,
    actions: Option<AnyElement>,
    cx: &App,
) -> AnyElement {
    let muted = cx.theme().muted_foreground;
    v_flex()
        .w_full()
        .items_center()
        .justify_center()
        .gap_2()
        .px_4()
        .py_8()
        .text_center()
        .child(Icon::new(icon).size(px(24.)).text_color(muted))
        .child(
            div()
                .max_w(px(300.))
                .font_weight(FontWeight::MEDIUM)
                .child(title.into()),
        )
        .child(
            div()
                .max_w(px(340.))
                .text_role(TextRole::Body, cx)
                .text_color(muted)
                .child(body.into()),
        )
        .children(actions)
        .into_any_element()
}
