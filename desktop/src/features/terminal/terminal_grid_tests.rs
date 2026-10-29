//! What the grid holds after the program's bytes: characters, their colours, and the cursor.

use alacritty_terminal::vte::ansi::{Color, NamedColor};

use super::TerminalGrid;

// Pins: terminal#3
#[test]
fn grid_renders_utf8_and_colors() {
    let mut grid = TerminalGrid::new(10, 3);
    grid.feed("\x1b[31mhé\x1b[0m".as_bytes());
    grid.feed(b"x");
    let first: Vec<_> = grid
        .cells()
        .into_iter()
        .filter(|cell| cell.line == 0)
        .collect();
    assert_eq!(
        first[..3]
            .iter()
            .map(|cell| (cell.text, cell.fg))
            .collect::<Vec<_>>(),
        vec![
            ('h', Color::Named(NamedColor::Red)),
            ('é', Color::Named(NamedColor::Red)),
            ('x', Color::Named(NamedColor::Foreground)),
        ]
    );
}

#[test]
fn cursor_follows_output() {
    let mut grid = TerminalGrid::new(10, 3);
    grid.feed(b"ab\r\ncd");
    assert_eq!(grid.cursor(), (2, 1));
    assert_eq!(grid.text(), "ab\ncd");
}
