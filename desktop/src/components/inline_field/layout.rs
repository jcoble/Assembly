//! One look for a labelled value, wherever it's edited: the label above it, the hint and error under it.
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

pub fn field_row(
    label: impl Into<SharedString>,
    hint: Option<SharedString>,
    error: Option<SharedString>,
    value: impl IntoElement,
    cx: &App,
) -> Div {
    let theme = cx.theme();
    let meta = |text: SharedString, color: Hsla| {
        div().text_role(TextRole::Meta, cx).text_color(color).child(text)
    };
    v_flex()
        .w_full()
        .min_w_0()
        .gap_1()
        .child(meta(label.into(), theme.muted_foreground))
        .child(value)
        .when_some(hint, |row, hint| row.child(meta(hint, theme.muted_foreground)))
        .when_some(error, |row, error| row.child(meta(error, theme.danger)))
}
