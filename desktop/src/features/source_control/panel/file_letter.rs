//! A changed file's status letter and line counts at the end of its row (the look's `.sl` and `.cnt`): the 12px mono
//! letter at 600, and the mono `+N −M` counts beside it (omitting any zero side). Called by the change sections' file
//! rows.

use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::SPACE;
use crate::core::theme::text::{TextRole, TextRoleExt as _};

/// The letter's column and the mono size it's set in.
const LETTER_WIDTH: Pixels = px(14.);
const MONO_SIZE: Pixels = px(12.);

pub fn file_letter(letter: char, cx: &App) -> Div {
    let theme = cx.theme();
    let tone = match letter {
        'A' | '?' => theme.success,
        'D' => theme.danger,
        'R' | 'C' => theme.primary,
        _ => Look::of(cx).text_2,
    };
    div()
        .w(LETTER_WIDTH)
        .flex_none()
        .text_center()
        .text_size(MONO_SIZE)
        .font_weight(FontWeight::SEMIBOLD)
        .font_family(theme.mono_font_family.clone())
        .text_color(tone)
        .child(letter.to_string())
}

/// A changed file's added and removed line counts beside its status letter, omitting any zero side.
pub fn file_counts(added: u32, removed: u32, cx: &App) -> Div {
    let look = Look::of(cx);
    h_flex()
        .flex_none()
        .gap(SPACE[1])
        .text_role(TextRole::Meta, cx)
        .font_family(cx.theme().mono_font_family.clone())
        .when(added > 0, |line| {
            line.child(div().text_color(look.ready).child(format!("+{added}")))
        })
        .when(removed > 0, |line| {
            line.child(div().text_color(look.problem).child(format!("−{removed}")))
        })
}
