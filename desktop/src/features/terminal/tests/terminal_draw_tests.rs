//! How a line's cells become runs of text.

use alacritty_terminal::vte::ansi::{Color, NamedColor};

use super::runs;
use crate::features::terminal::terminal_grid::TerminalGrid;

#[test]
fn a_wide_character_takes_two_columns_and_no_blank() {
    let mut grid = TerminalGrid::new(10, 2);
    grid.feed("中a".as_bytes());
    let runs = runs(
        grid.cells()
            .into_iter()
            .filter(|cell| cell.line == 0)
            .collect(),
        None,
    );
    let runs: Vec<_> = runs
        .iter()
        .map(|run| (run.text.clone(), run.columns))
        .collect();
    // 中 covers columns 0 and 1, a is column 2, and seven blanks fill the line.
    assert_eq!(runs, vec![(format!("中a{}", " ".repeat(7)), 10)]);
    assert_eq!(Color::Named(NamedColor::Foreground), grid.cells()[0].fg);
}

fn first_line(bytes: &[u8]) -> Vec<super::Run> {
    let mut grid = TerminalGrid::new(4, 2);
    grid.feed(bytes);
    let line = (grid.cells().into_iter())
        .filter(|cell| cell.line == 0)
        .collect();
    runs(line, None)
}

#[test]
fn a_combining_mark_stays_with_its_cell() {
    let runs = first_line("e\u{301}xyz".as_bytes());
    assert_eq!(runs.len(), 1);
    assert_eq!(runs[0].text, "e\u{301}xyz");
    assert_eq!(runs[0].columns, 4);
}

#[test]
fn a_multi_codepoint_character_stays_in_one_cell() {
    // A heart with its emoji selector, and a letter with two marks: three characters over two cells.
    let runs = first_line("\u{2764}\u{fe0f}e\u{301}\u{323}x".as_bytes());
    assert_eq!(runs.len(), 1);
    assert_eq!(runs[0].text, "\u{2764}\u{fe0f}e\u{301}\u{323}x ");
    assert_eq!(runs[0].columns, 4);
}

#[test]
fn a_run_breaks_where_the_style_changes() {
    for (code, look) in [
        ("3", (|run| run.italic) as fn(&super::Run) -> bool),
        ("4", |run| run.underline),
        ("9", |run| run.strike),
        ("2", |run| run.dim),
    ] {
        let line = format!("ab\x1b[{code}mcd");
        let runs = first_line(line.as_bytes());
        let pieces: Vec<_> = runs.iter().map(|run| run.text.as_str()).collect();
        assert_eq!(pieces, ["ab", "cd"], "SGR {code}");
        assert!(!look(&runs[0]) && look(&runs[1]), "SGR {code}");
    }
}

#[test]
fn hidden_text_is_a_blank_of_the_same_width() {
    let runs = first_line("\x1b[8;4;9mae\u{301}b\x1b[0mz".as_bytes());
    assert_eq!(runs[0].text, "   ", "no text and no marks");
    assert_eq!(runs[0].columns, 3);
    assert!(!runs[0].underline && !runs[0].strike, "and no lines drawn");
    assert_eq!(runs[1].text, "z");
}
