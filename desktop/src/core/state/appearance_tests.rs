use gpui_kit::px;
use serde_json::{Value, json};

use super::*;
use crate::core::theme::theme_id::ThemeId;

fn saved(pairs: &[(&str, Value)]) -> Appearance {
    let mut appearance = Appearance::default();
    for (key, value) in pairs {
        appearance.set(key.to_string(), value.clone());
    }
    appearance
}

#[test]
fn nothing_saved_gives_the_defaults() {
    let appearance = Appearance::default();
    assert_eq!(appearance.theme(), ThemeId::Assembly);
    assert_eq!(appearance.ui_font(), SYSTEM_FONT);
    assert_eq!(appearance.code_font(), DEFAULT_CODE_FONT);
    assert_eq!(appearance.editor_font(), DEFAULT_CODE_FONT);
    assert_eq!(appearance.terminal_font(), DEFAULT_CODE_FONT);
    assert_eq!(appearance.number(&UI_FONT_SIZE), 16.);
    assert_eq!(appearance.number(&EDITOR_FONT_SIZE), 13.);
    assert_eq!(appearance.number(&EDITOR_LINE_HEIGHT), 20.);
    assert_eq!(appearance.number(&TERMINAL_FONT_SIZE), 15.);
    assert_eq!(appearance.number(&TERMINAL_LINE_HEIGHT), 1.2);
}

#[test]
fn each_saved_value_reads_back_under_its_key() {
    let appearance = saved(&[
        (THEME, json!("dracula")),
        (UI_FONT, json!("Inter")),
        (CODE_FONT, json!("Fira Code")),
        (EDITOR_FONT, json!("JetBrains Mono")),
        (TERMINAL_FONT, json!("IBM Plex Mono")),
        (UI_FONT_SIZE.key, json!(15)),
        (EDITOR_FONT_SIZE.key, json!(16)),
        (EDITOR_LINE_HEIGHT.key, json!(26)),
        (TERMINAL_FONT_SIZE.key, json!(12)),
        (TERMINAL_LINE_HEIGHT.key, json!(1.5)),
    ]);
    assert_eq!(appearance.theme(), ThemeId::Dracula);
    assert_eq!(appearance.ui_font(), "Inter");
    assert_eq!(appearance.code_font(), "Fira Code");
    assert_eq!(appearance.editor_font(), "JetBrains Mono");
    assert_eq!(appearance.terminal_font(), "IBM Plex Mono");
    assert_eq!(appearance.number(&UI_FONT_SIZE), 15.);
    assert_eq!(appearance.number(&EDITOR_FONT_SIZE), 16.);
    assert_eq!(appearance.number(&EDITOR_LINE_HEIGHT), 26.);
    assert_eq!(appearance.number(&TERMINAL_FONT_SIZE), 12.);
    assert_eq!(appearance.number(&TERMINAL_LINE_HEIGHT), 1.5);
}

// Appearance's Code font is the shared default: the editor and terminal use it until they're given their own.
#[test]
fn the_editor_and_terminal_follow_the_code_font_until_set() {
    let mut appearance = saved(&[(CODE_FONT, json!("Fira Code"))]);
    assert_eq!(appearance.editor_font(), "Fira Code");
    assert_eq!(appearance.terminal_font(), "Fira Code");
    appearance.set(EDITOR_FONT.into(), json!("Menlo"));
    assert_eq!(appearance.editor_font(), "Menlo");
    assert_eq!(appearance.terminal_font(), "Fira Code");
}

// A reset saves null, and a value of the wrong kind or an unknown theme reads as the default.
#[test]
fn a_reset_or_a_value_that_doesnt_read_gives_the_default() {
    let appearance = saved(&[
        (THEME, json!("dark")),
        (CODE_FONT, Value::Null),
        (EDITOR_FONT, json!("  ")),
        (UI_FONT_SIZE.key, json!("big")),
        (EDITOR_FONT_SIZE.key, Value::Null),
    ]);
    assert_eq!(appearance.theme(), ThemeId::Assembly);
    assert_eq!(appearance.code_font(), DEFAULT_CODE_FONT);
    assert_eq!(appearance.editor_font(), DEFAULT_CODE_FONT);
    assert_eq!(appearance.number(&UI_FONT_SIZE), 16.);
    assert_eq!(appearance.number(&EDITOR_FONT_SIZE), 13.);
}

// A number past its slider's ends is held at the end; one between steps goes to the nearest step.
#[test]
fn a_number_stays_in_its_range_and_on_a_step() {
    let appearance = saved(&[
        (UI_FONT_SIZE.key, json!(40)),
        (EDITOR_LINE_HEIGHT.key, json!(2)),
        (EDITOR_FONT_SIZE.key, json!(14.4)),
        (TERMINAL_LINE_HEIGHT.key, json!(1.26)),
    ]);
    assert_eq!(appearance.number(&UI_FONT_SIZE), 18.);
    assert_eq!(appearance.number(&EDITOR_LINE_HEIGHT), 14.);
    assert_eq!(appearance.number(&EDITOR_FONT_SIZE), 14.);
    assert!((appearance.number(&TERMINAL_LINE_HEIGHT) - 1.3).abs() < 1e-4);
}

#[test]
fn a_number_shows_with_its_unit() {
    assert_eq!(EDITOR_FONT_SIZE.label(13.), "13px");
    assert_eq!(TERMINAL_LINE_HEIGHT.label(1.2), "1.2×");
}

#[test]
fn the_terminal_draws_in_its_own_font_size_and_line_spacing() {
    let appearance = saved(&[
        (TERMINAL_FONT, json!("Fira Code")),
        (TERMINAL_FONT_SIZE.key, json!(14)),
        (TERMINAL_LINE_HEIGHT.key, json!(1.5)),
    ]);
    let (font, size, line_height) = appearance.terminal_text();
    assert_eq!(
        (font.as_ref(), size, line_height),
        ("Fira Code", px(14.), px(21.))
    );
}

#[test]
fn ui_line_height_defaults_to_1_45_and_clamps() {
    assert!((Appearance::default().number(&UI_LINE_HEIGHT) - 1.45).abs() < 1e-5);
    let low = saved(&[(UI_LINE_HEIGHT.key, json!(0.5))]);
    let high = saved(&[(UI_LINE_HEIGHT.key, json!(3.0))]);
    assert_eq!(low.number(&UI_LINE_HEIGHT), 1.2);
    assert_eq!(high.number(&UI_LINE_HEIGHT), 1.8);
}

#[test]
fn text_roles_scale_with_the_interface_size() {
    use crate::core::theme::text::TextRole;
    let appearance = saved(&[
        (UI_FONT_SIZE.key, json!(16)),
        (UI_LINE_HEIGHT.key, json!(1.5)),
    ]);
    for (role, base) in [
        (TextRole::Meta, 13.),
        (TextRole::Body, 14.),
        (TextRole::Title, 15.),
        (TextRole::Chat, 16.),
        (TextRole::Heading, 18.),
        (TextRole::Display, 20.),
    ] {
        let (size, height) = appearance.ui_text(role);
        let expected = 16. * base / 16.;
        assert!((size - px(expected)).abs() < px(0.001));
        let line_height = if role == TextRole::Chat { 1.6 } else { 1.5 };
        assert!((height - px(expected * line_height)).abs() < px(0.001));
    }
}

#[test]
fn role_size_follows_the_interface_size() {
    use crate::core::theme::text::{TextRole, kit_size};
    use gpui_kit::component::{Size, StyleSized as _};
    use gpui_kit::{Styled as _, div};
    for (interface, expected) in [(16, 14.), (13, 11.375), (18, 15.75)] {
        let appearance = saved(&[(UI_FONT_SIZE.key, json!(interface))]);
        let size = kit_size(appearance.ui_text(TextRole::Body).0);
        assert!(
            matches!(size, Size::Size(_)),
            "the kit reads it as a custom size"
        );
        let mut text = div().input_text_size(size);
        let drawn = text
            .text_style()
            .font_size
            .expect("the kit sets a text size");
        assert_eq!(drawn, px(expected).into());
    }
}

#[test]
fn chat_line_height_keeps_its_reading_ratio() {
    use crate::core::theme::text::TextRole;
    let compact = saved(&[(UI_LINE_HEIGHT.key, json!(1.2))]);
    let roomy = saved(&[(UI_LINE_HEIGHT.key, json!(1.8))]);
    assert_eq!(
        compact.ui_text(TextRole::Chat),
        roomy.ui_text(TextRole::Chat)
    );
    assert_ne!(
        compact.ui_text(TextRole::Meta),
        roomy.ui_text(TextRole::Meta)
    );
}

#[test]
fn interface_leading_keeps_its_half_tenth_in_the_slider_label() {
    assert_eq!(UI_LINE_HEIGHT.label(1.45), "1.45×");
    assert_eq!(TERMINAL_LINE_HEIGHT.label(1.2), "1.2×");
}
