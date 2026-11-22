//! A changed file's status letter at the end of its row (the look's `.sl`): the 12px mono letter at 600, an added or
//! new file in the success colour, a deletion in the danger colour, a rename or copy in the primary colour and an edit
//! in text-2. Called by the change sections' file rows.

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::*;

use crate::core::theme::look::Look;

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
