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

use super::terminal_links::url_at;

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
    pub italic: bool,
    pub underline: bool,
    pub strike: bool,
    pub dim: bool,
    pub hidden: bool,
    /// Combining marks that stay with this cell's character.
    pub marks: Vec<char>,
}

/// A link under the mouse: where it shows, as (screen line, start column, end column), and where it goes.
#[derive(Clone, Debug, PartialEq)]
pub struct HoveredLink {
    pub line_spans: Vec<(usize, usize, usize)>,
    pub url: String,
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

    /// Moves the view `lines` back into the history, or forward when negative; it stops at either end. Says whether
    /// the view moved.
    pub fn scroll(&mut self, lines: i32) -> bool {
        self.moved(Scroll::Delta(lines))
    }

    /// Says whether the view moved.
    pub fn scroll_to_bottom(&mut self) -> bool {
        self.moved(Scroll::Bottom)
    }

    fn moved(&mut self, scroll: Scroll) -> bool {
        let offset = self.display_offset();
        self.term.scroll_display(scroll);
        self.display_offset() != offset
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

    /// Selects the word at a cell of the screen as shown, and says whether there was one; a blank clears the
    /// selection. A word is ASCII letters, digits, `_` and `-`, on that row only, as the old renderer's
    /// `getWordAtCell` (`/[\w-]/`).
    pub fn select_word(&mut self, column: usize, line: usize) -> bool {
        let (point, _) = self.point(column, line, false);
        let row = &self.term.grid()[point.line];
        let word = |column: usize| {
            let c = row[Column(column)].c;
            c.is_ascii_alphanumeric() || c == '_' || c == '-'
        };
        if column >= self.term.columns() || !word(column) {
            self.clear_selection();
            return false;
        }
        let start = (0..column).rev().take_while(|&at| word(at)).last();
        let end = (column + 1..self.term.columns())
            .take_while(|&at| word(at))
            .last();
        let at = |column| Point::new(point.line, Column(column));
        let mut selection = Selection::new(
            SelectionType::Simple,
            at(start.unwrap_or(column)),
            Side::Left,
        );
        selection.update(at(end.unwrap_or(column)), Side::Right);
        self.term.selection = Some(selection);
        true
    }

    /// Says whether there was a selection to clear.
    pub fn clear_selection(&mut self) -> bool {
        self.term.selection.take().is_some()
    }

    /// The lines kept above the screen.
    pub fn history_size(&self) -> usize {
        self.term.grid().history_size()
    }

    /// How many lines back the view is.
    pub fn display_offset(&self) -> usize {
        self.term.grid().display_offset()
    }

    /// Moves the view to `offset` lines back, stopping at either end.
    pub fn scroll_to_offset(&mut self, offset: usize) {
        let by = offset as i32 - self.display_offset() as i32;
        self.scroll(by);
    }

    /// The link at a cell of the screen as shown: an OSC 8 link, its cells joined across rows, or else a URL printed
    /// on that row. A `file:` OSC 8 link gives none.
    pub fn link_at(&self, column: usize, line: usize) -> Option<HoveredLink> {
        let (columns, lines) = (self.term.columns(), self.term.screen_lines());
        if column >= columns || line >= lines {
            return None;
        }
        let offset = self.display_offset() as i32;
        let grid = self.term.grid();
        let cell =
            |index: usize| &grid[Line((index / columns) as i32 - offset)][Column(index % columns)];
        let at = line * columns + column;
        if let Some(hyperlink) = cell(at).hyperlink() {
            if hyperlink
                .uri()
                .get(..5)
                .is_some_and(|scheme| scheme.eq_ignore_ascii_case("file:"))
            {
                return None;
            }
            let same = |index: &usize| cell(*index).hyperlink().as_ref() == Some(&hyperlink);
            let first = (0..at).rev().take_while(same).last().unwrap_or(at);
            let last = (at..columns * lines).take_while(same).last().unwrap_or(at);
            let line_spans = (first / columns..=last / columns)
                .map(|row| {
                    let start = if row == first / columns {
                        first % columns
                    } else {
                        0
                    };
                    let end = if row == last / columns {
                        last % columns + 1
                    } else {
                        columns
                    };
                    (row, start, end)
                })
                .collect();
            let url = hyperlink.uri().to_string();
            return Some(HoveredLink { line_spans, url });
        }
        let row: String = (0..columns)
            .map(|at| match cell(line * columns + at).c {
                c if c < ' ' => ' ',
                c => c,
            })
            .collect();
        let (start, end, url) = url_at(&row, column)?;
        Some(HoveredLink {
            line_spans: vec![(line, start, end)],
            url,
        })
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
                    italic: cell.flags.contains(Flags::ITALIC),
                    underline: cell.flags.intersects(Flags::ALL_UNDERLINES),
                    strike: cell.flags.contains(Flags::STRIKEOUT),
                    dim: cell.flags.contains(Flags::DIM),
                    hidden: cell.flags.contains(Flags::HIDDEN),
                    marks: cell.zerowidth().map(<[char]>::to_vec).unwrap_or_default(),
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
