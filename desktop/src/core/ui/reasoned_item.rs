//! A menu row that can't run stays visible and says why on a second line, because a disabled row shows no tooltip.
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::menu::PopupMenuItem;
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;

/// A reason wraps at this width rather than stretch its menu past the window's edge.
const REASON_WIDTH: Pixels = px(256.);

pub fn reasoned_item(
    label: impl Into<SharedString>,
    reason: Option<String>,
    on_click: impl Fn(&mut Window, &mut App) + 'static,
) -> PopupMenuItem {
    let label = label.into();
    match reason {
        None => PopupMenuItem::new(label).on_click(move |_, window, cx| on_click(window, cx)),
        Some(reason) => PopupMenuItem::element(move |_, cx| {
            v_flex().child(label.clone()).child(
                div()
                    .w(REASON_WIDTH)
                    .whitespace_normal()
                    .text_role(TextRole::Body, cx)
                    .text_color(cx.theme().muted_foreground)
                    .child(reason.clone()),
            )
        })
        .disabled(true),
    }
}
