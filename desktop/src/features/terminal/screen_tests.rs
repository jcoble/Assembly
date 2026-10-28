use gpui_kit::component::Theme;

use super::{Screen, Size};
use crate::features::terminal::palette::{Palette, hsla, rgb};

fn screen() -> Screen {
    Screen::new(Size { cols: 20, rows: 4 })
}

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

fn text(screen: &mut Screen, line: usize) -> String {
    screen.lines(&palette(), false)[line]
        .text
        .trim_end()
        .to_string()
}

// Pins: terminal#3
#[test]
fn a_character_split_across_two_reads_draws_whole() {
    let mut screen = screen();
    let bytes = "caf\u{e9}".as_bytes();
    screen.feed(&bytes[..4]);
    screen.feed(&bytes[4..]);
    assert_eq!(text(&mut screen, 0), "caf\u{e9}");
}

// Pins: terminal#11
#[test]
fn colored_output_draws_in_the_themes_colors() {
    let mut screen = screen();
    screen.feed(b"\x1b[31mred\x1b[0m plain");
    let palette = palette();
    let line = &screen.lines(&palette, false)[0];
    assert_eq!(line.runs[0].0, 3);
    // Through Ghostty, which keeps colors as 8-bit RGB.
    let through = |color| hsla(rgb(color, palette.background));
    assert_eq!(line.runs[0].1.foreground, through(theme().colors.red));
    assert_eq!(line.runs[1].1.foreground, through(palette.foreground));
}

// Pins: terminal#2
#[test]
fn a_programs_questions_are_answered_and_its_modes_read() {
    let mut screen = screen();
    screen.feed(b"ab\x1b[6n");
    assert_eq!(screen.replies(), b"\x1b[1;3R");
    assert!(screen.replies().is_empty(), "each reply once");
    screen.feed(b"\x1b[c");
    assert_eq!(
        screen.replies(),
        b"\x1b[?62;22c",
        "device attributes, as Ghostty answers"
    );
    assert!(!screen.app_cursor() && !screen.bracketed_paste());
    screen.feed(b"\x1b[?1h\x1b[?2004h");
    assert!(screen.app_cursor() && screen.bracketed_paste());
}

// Pins: terminal#4
#[test]
fn resizing_rewraps_to_the_new_size() {
    let mut screen = screen();
    screen.resize(Size { cols: 30, rows: 6 });
    assert_eq!(screen.size(), Size { cols: 30, rows: 6 });
    assert_eq!(screen.lines(&palette(), false).len(), 6);
}

// Pins: terminal#2
#[test]
fn the_cursor_cell_is_drawn_in_the_cursor_color_when_shown() {
    let mut screen = screen();
    screen.feed(b"x");
    let palette = palette();
    let line = &screen.lines(&palette, true)[0];
    assert_eq!(line.runs[1].1.background, Some(palette.cursor));
    let hidden = &screen.lines(&palette, false)[0];
    assert_eq!(hidden.runs.len(), 1);
}
