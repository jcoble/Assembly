use gpui_kit::component::Theme;
use libghostty_vt::style::RgbColor;

use super::{Palette, rgb};

fn palette() -> Palette {
    Palette::from_theme(&theme())
}

/// The default theme's colors are all the same, so the tests give it a dark background, light text and a red.
fn theme() -> Theme {
    let mut theme = Theme::default();
    theme.colors.background = gpui_kit::hsla(0., 0., 0.1, 1.);
    theme.colors.foreground = gpui_kit::hsla(0., 0., 0.9, 1.);
    theme.colors.red = gpui_kit::hsla(0., 0.8, 0.5, 1.);
    theme
}

// Pins: terminal#11
#[test]
fn the_text_background_cursor_and_ansi_colors_are_the_themes() {
    let theme = theme();
    let palette = palette();
    assert_eq!(palette.foreground, theme.colors.foreground);
    assert_eq!(palette.background, theme.colors.background);
    assert_eq!(palette.cursor, theme.colors.caret);
    assert_eq!(
        palette.colors[1],
        rgb(theme.colors.red, theme.colors.background)
    );
}

// Pins: terminal#11
#[test]
fn black_is_dark_and_white_light_in_any_theme() {
    let palette = palette();
    let (black, white) = (palette.colors[0], palette.colors[15]);
    assert!(black.r < white.r);
}

// Pins: terminal#11
#[test]
fn past_16_come_the_256_color_cube_and_grays() {
    let palette = palette();
    assert_eq!(palette.colors[196], RgbColor { r: 255, g: 0, b: 0 });
    assert_eq!(palette.colors[232], RgbColor { r: 8, g: 8, b: 8 });
}
