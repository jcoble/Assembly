//! `TerminalGrid` holds what a terminal shows: `alacritty_terminal`'s grid and the parser that feeds it the program's
//! bytes. It draws nothing; terminal_draw.rs paints its `cells()`.

use alacritty_terminal::Term;
use alacritty_terminal::event::VoidListener;
use alacritty_terminal::grid::{Dimensions, Scroll};
use alacritty_terminal::index::{Column, Line, Point, Side};
use alacritty_terminal::selection::{Selection, SelectionType};
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
    /// Inside the mouse selection.
    pub selected: bool,
}

/// The lines kept above the screen to scroll back to, as the old app's terminal kept.
const HISTORY: usize = 8000;

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
            term: Term::new(
                Config {
                    scrolling_history: HISTORY,
                    ..Config::default()
                },
                &Cells(cols, rows),
                VoidListener,
            ),
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

    /// Moves the view `lines` back into the history, or forward when negative; it stops at either end.
    pub fn scroll(&mut self, lines: i32) {
        self.term.scroll_display(Scroll::Delta(lines));
    }

    pub fn scroll_to_bottom(&mut self) {
        self.term.scroll_display(Scroll::Bottom);
    }

    /// A full-screen program (an editor, a pager) shows its own screen with no history: the wheel goes to it as
    /// arrow keys, as xterm sends them.
    pub fn wheel_sends_arrows(&self) -> bool {
        (self.term.mode()).contains(TermMode::ALT_SCREEN | TermMode::ALTERNATE_SCROLL)
    }

    /// Starts a selection at a cell of the screen as shown, on its left or right half.
    pub fn select_start(&mut self, column: usize, line: usize, right: bool) {
        let (point, side) = self.point(column, line, right);
        self.term.selection = Some(Selection::new(SelectionType::Simple, point, side));
    }

    /// Moves the selection's other end.
    pub fn select_to(&mut self, column: usize, line: usize, right: bool) {
        let (point, side) = self.point(column, line, right);
        if let Some(selection) = &mut self.term.selection {
            selection.update(point, side);
        }
    }

    /// The selected text, lines joined by line breaks; none when nothing is selected.
    pub fn selection_text(&self) -> Option<String> {
        self.term
            .selection_to_string()
            .filter(|text| !text.is_empty())
    }

    /// A cell of the screen as shown, as a point of the whole grid, history included.
    fn point(&self, column: usize, line: usize, right: bool) -> (Point, Side) {
        let offset = self.term.grid().display_offset() as i32;
        let point = Point::new(Line(line as i32 - offset), Column(column));
        (point, if right { Side::Right } else { Side::Left })
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
        let selection = content.selection;
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
                    selected: selection.is_some_and(|range| range.contains(cell.point)),
                }
            })
            .collect()
    }

    /// The cursor's column and screen line, while its line is on the screen.
    pub fn cursor(&self) -> Option<(usize, usize)> {
        let grid = self.term.grid();
        let line = grid.cursor.point.line.0 + grid.display_offset() as i32;
        // Scrolled back far enough, the cursor's line is below the screen.
        (line < grid.screen_lines() as i32)
            .then(|| (grid.cursor.point.column.0, line.max(0) as usize))
    }

    /// Mouse tracking and encoding modes owned by alacritty's parser.
    pub fn mouse_mode(&self) -> TermMode {
        *self.term.mode()
    }

    /// The program asked for application cursor keys, so arrows send `ESC O` rather than `ESC [`.
    pub fn app_cursor(&self) -> bool {
        self.term.mode().contains(TermMode::APP_CURSOR)
    }

    /// The program asked for pastes to come between `ESC [200~` and `ESC [201~`.
    pub fn bracketed_paste(&self) -> bool {
        self.term.mode().contains(TermMode::BRACKETED_PASTE)
    }

    /// The program asked to hear `ESC [I` when the terminal takes the focus and `ESC [O` when it loses it.
    pub fn focus_reporting(&self) -> bool {
        self.term.mode().contains(TermMode::FOCUS_IN_OUT)
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
