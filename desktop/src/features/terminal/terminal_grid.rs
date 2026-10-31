//! `TerminalGrid` holds what a terminal shows: `alacritty_terminal`'s grid and the parser that feeds it the program's
//! bytes. It draws nothing; terminal_draw.rs paints its `cells()`.

use alacritty_terminal::Term;
use alacritty_terminal::event::VoidListener;
use alacritty_terminal::grid::Dimensions;
use alacritty_terminal::term::cell::Flags;
use alacritty_terminal::term::{Config, TermMode};
use alacritty_terminal::vte::ansi::{Color, Processor};

pub struct TerminalGrid {
    term: Term<VoidListener>,
    parser: Processor,
}

/// One cell to paint: its character, colours and boldness, at its column and screen line.
#[derive(Clone, Debug, PartialEq)]
pub struct GridCell {
    pub column: usize,
    pub line: usize,
    pub text: char,
    pub fg: Color,
    pub bg: Color,
    pub bold: bool,
    /// A double-width character, which covers this column and the next.
    pub wide: bool,
}

/// A size in cells, which is how `Term` is told one.
struct Cells(usize, usize);

impl Dimensions for Cells {
    fn total_lines(&self) -> usize {
        self.1
    }

    fn screen_lines(&self) -> usize {
        self.1
    }

    fn columns(&self) -> usize {
        self.0
    }
}

impl TerminalGrid {
    pub fn new(cols: usize, rows: usize) -> Self {
        Self {
            term: Term::new(Config::default(), &Cells(cols, rows), VoidListener),
            parser: Processor::new(),
        }
    }

    /// The program's bytes, as they came: the parser keeps a character or escape split across two feeds until the
    /// rest arrives (terminal#3).
    pub fn feed(&mut self, bytes: &[u8]) {
        self.parser.advance(&mut self.term, bytes);
    }

    pub fn resize(&mut self, cols: usize, rows: usize) {
        self.term.resize(Cells(cols, rows));
    }

    /// Its size in columns and rows.
    #[cfg(test)]
    pub fn size(&self) -> (usize, usize) {
        (self.term.columns(), self.term.screen_lines())
    }

    /// The screen's cells, line by line, leaving out the second half of a wide character. An inverse cell comes with
    /// its colours swapped.
    pub fn cells(&self) -> Vec<GridCell> {
        let content = self.term.renderable_content();
        let offset = content.display_offset as i32;
        content
            .display_iter
            .filter(|cell| {
                !cell
                    .flags
                    .intersects(Flags::WIDE_CHAR_SPACER | Flags::LEADING_WIDE_CHAR_SPACER)
            })
            .map(|cell| {
                let (fg, bg) = match cell.flags.contains(Flags::INVERSE) {
                    true => (cell.bg, cell.fg),
                    false => (cell.fg, cell.bg),
                };
                GridCell {
                    column: cell.point.column.0,
                    line: (cell.point.line.0 + offset) as usize,
                    text: cell.c,
                    fg,
                    bg,
                    bold: cell.flags.contains(Flags::BOLD),
                    wide: cell.flags.contains(Flags::WIDE_CHAR),
                }
            })
            .collect()
    }

    /// The cursor's column and screen line.
    pub fn cursor(&self) -> (usize, usize) {
        let point = self.term.grid().cursor.point;
        (point.column.0, point.line.0.max(0) as usize)
    }

    /// The program asked for application cursor keys, so arrows send `ESC O` rather than `ESC [`.
    pub fn app_cursor(&self) -> bool {
        self.term.mode().contains(TermMode::APP_CURSOR)
    }

    /// The program asked for pastes to come between `ESC [200~` and `ESC [201~`.
    pub fn bracketed_paste(&self) -> bool {
        self.term.mode().contains(TermMode::BRACKETED_PASTE)
    }

    /// The screen as text, each line's trailing blanks dropped and the blank lines at the end too.
    #[cfg(test)]
    pub fn text(&self) -> String {
        let (_, rows) = self.size();
        let mut lines = vec![String::new(); rows];
        for cell in self.cells() {
            lines[cell.line].push(cell.text);
        }
        let lines: Vec<_> = lines.iter().map(|line| line.trim_end()).collect();
        lines.join("\n").trim_end().to_string()
    }
}

#[cfg(test)]
#[path = "terminal_grid_tests.rs"]
mod tests;
