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
