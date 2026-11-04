//! A menu row that can't run stays visible and says why on a second line, because a disabled row shows no tooltip.
use gpui_kit::component::menu::PopupMenuItem;
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;

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
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child(reason.clone()),
            )
        })
        .disabled(true),
    }
}
