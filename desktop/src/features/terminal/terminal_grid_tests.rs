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
    assert_eq!(grid.cursor(), Some((2, 1)));
    assert_eq!(grid.text(), "ab\ncd");
}

#[test]
fn scrolling_back_shows_earlier_lines() {
    let mut grid = TerminalGrid::new(10, 3);
    grid.feed(b"1\r\n2\r\n3\r\n4\r\n5");
    assert_eq!(grid.text(), "3\n4\n5");
    grid.scroll(2);
    assert_eq!(grid.text(), "1\n2\n3");
    grid.scroll(5);
    assert_eq!(grid.text(), "1\n2\n3");
    grid.scroll_to_bottom();
    assert_eq!(grid.text(), "3\n4\n5");
}

#[test]
fn a_selection_reads_as_text_and_marks_its_cells() {
    let mut grid = TerminalGrid::new(12, 3);
    grid.feed(b"hello world\r\nnext");
    grid.select_start(0, 0, false);
    grid.select_to(4, 0, true);
    assert_eq!(grid.selection_text().as_deref(), Some("hello"));
    let selected: String = (grid.cells().into_iter())
        .filter(|cell| cell.selected)
        .map(|cell| cell.text)
        .collect();
    assert_eq!(selected, "hello");
    grid.select_start(6, 0, false);
    grid.select_to(3, 1, true);
    assert_eq!(grid.selection_text().as_deref(), Some("world\nnext"));
    grid.select_start(2, 1, false);
    assert_eq!(grid.selection_text(), None);
}

#[test]
fn a_selection_made_while_scrolled_back_reads_the_earlier_lines() {
    let mut grid = TerminalGrid::new(10, 2);
    grid.feed(b"one\r\ntwo\r\nthree");
    grid.scroll(1);
    grid.select_start(0, 0, false);
    grid.select_to(2, 0, true);
    assert_eq!(grid.selection_text().as_deref(), Some("one"));
}

fn first_cell(bytes: &[u8]) -> super::GridCell {
    let mut grid = TerminalGrid::new(10, 3);
    grid.feed(bytes);
    grid.cells().into_iter().next().unwrap()
}

#[test]
fn each_style_reaches_a_cell() {
    assert!(first_cell(b"\x1b[3mx").italic);
    assert!(first_cell(b"\x1b[4mx").underline);
    assert!(first_cell(b"\x1b[9mx").strike);
    assert!(first_cell(b"\x1b[2mx").dim);
    assert!(first_cell(b"\x1b[8mx").hidden);
    let plain = first_cell(b"x");
    assert!(!plain.italic && !plain.underline && !plain.strike && !plain.dim && !plain.hidden);
}

#[test]
fn a_double_underline_is_an_underline() {
    assert!(first_cell(b"\x1b[21mx").underline);
}

#[test]
fn a_combining_mark_stays_with_its_cell() {
    let mut grid = TerminalGrid::new(10, 3);
    grid.feed("e\u{301}x".as_bytes());
    let cells = grid.cells();
    assert_eq!(
        (cells[0].text, cells[0].marks.clone()),
        ('e', vec!['\u{301}'])
    );
    assert_eq!(
        (cells[1].column, cells[1].text),
        (1, 'x'),
        "it takes no column"
    );
}
