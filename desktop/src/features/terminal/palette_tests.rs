use alacritty_terminal::term::color::Colors;
use alacritty_terminal::vte::ansi::{Color, NamedColor, Rgb};
use gpui_kit::component::Theme;

use super::{Palette, hsla};

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
fn the_text_background_and_cursor_are_the_themes() {
    let theme = theme();
    let palette = palette();
    let set = Colors::default();
    assert_eq!(
        palette.color(Color::Named(NamedColor::Foreground), &set),
        theme.colors.foreground
    );
    assert_eq!(
        palette.color(Color::Named(NamedColor::Background), &set),
        theme.colors.background
    );
    assert_eq!(
        palette.color(Color::Named(NamedColor::Red), &set),
        theme.colors.red
    );
    assert_eq!(
        palette.color(Color::Indexed(1), &set),
        theme.colors.red,
        "indexes below 16 are the ANSI colors"
    );
}

// Pins: terminal#11
#[test]
fn black_is_dark_and_white_light_in_any_theme() {
    let palette = palette();
    let set = Colors::default();
    let black = palette.color(Color::Named(NamedColor::Black), &set);
    let white = palette.color(Color::Named(NamedColor::BrightWhite), &set);
    assert!(black.l < white.l);
}

// Pins: terminal#11
#[test]
fn the_256_color_cube_true_color_and_a_programs_own_color() {
    let palette = palette();
    let mut set = Colors::default();
    let red = Rgb { r: 255, g: 0, b: 0 };
    assert_eq!(palette.color(Color::Indexed(196), &set), hsla(red));
    assert_eq!(
        palette.color(Color::Indexed(232), &set),
        hsla(Rgb { r: 8, g: 8, b: 8 })
    );
    let spec = Rgb { r: 1, g: 2, b: 3 };
    assert_eq!(palette.color(Color::Spec(spec), &set), hsla(spec));
    set[NamedColor::Red as usize] = Some(spec);
    assert_eq!(
        palette.color(Color::Named(NamedColor::Red), &set),
        hsla(spec)
    );
}
