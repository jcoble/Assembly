//! `match_text` shows a search result's words with what was typed picked out, as the look's ⌘K search draws them: the
//! typed letters in text-1 at 650 over the row's text-2. It is GPUI's styled text, because the kit's `Label` can only
//! recolour a match in the theme's blue (gpui-component 0.9.0 `label.rs:179-186`), not set its weight.

use crate::core::theme::look::Look;
use gpui_kit::*;
use std::ops::Range;

/// The weight of the typed letters.
const MATCH_WEIGHT: FontWeight = FontWeight(650.);

/// `text` with each place `typed` appears in it (ignoring case) drawn strong; the caller sets the text-2 around it.
pub fn match_text(text: impl Into<SharedString>, typed: &str, cx: &App) -> StyledText {
    let text: SharedString = text.into();
    let strong = HighlightStyle {
        color: Some(Look::of(cx).text_1),
        font_weight: Some(MATCH_WEIGHT),
        ..Default::default()
    };
    let ranges = matches(&text, typed);
    StyledText::new(text).with_highlights(ranges.into_iter().map(|range| (range, strong)))
}

/// Where `typed` appears in `text`, ignoring ASCII case, as byte ranges. Lower-casing ASCII keeps every byte where it
/// was, so the ranges fit the original text.
fn matches(text: &str, typed: &str) -> Vec<Range<usize>> {
    if typed.is_empty() {
        return Vec::new();
    }
    let (text, typed) = (text.to_ascii_lowercase(), typed.to_ascii_lowercase());
    text.match_indices(&typed)
        .map(|(at, _)| at..at + typed.len())
        .collect()
}
