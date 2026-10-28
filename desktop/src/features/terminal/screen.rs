//! `Screen` owns one terminal's emulation in the window: the bytes go into alacritty's VT parser and grid (the only
//! code that reads escape sequences), and it lays the visible grid out as styled lines for the pane to draw. What
//! the program asks the terminal to answer (a cursor position report, device attributes) comes back as `replies`, to
//! write to it. Replaces ghosttyFactory.ts's terminal and its fit.

use std::sync::{Arc, Mutex};

use alacritty_terminal::Term;
use alacritty_terminal::event::{Event, EventListener};
use alacritty_terminal::grid::{Dimensions, Scroll};
use alacritty_terminal::term::cell::Flags;
use alacritty_terminal::term::{Config, TermMode};
use alacritty_terminal::vte::ansi::{Color, NamedColor, Processor};

use super::palette::Palette;

/// A size in cells, as the pane measured it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Size {
    pub cols: u16,
    pub rows: u16,
}

impl Dimensions for Size {
    fn total_lines(&self) -> usize {
        self.rows as usize
    }

    fn screen_lines(&self) -> usize {
        self.rows as usize
    }

    fn columns(&self) -> usize {
        self.cols as usize
    }
}

/// One cell's look, which a line groups into runs.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Look {
    pub foreground: gpui_kit::Hsla,
    /// `None` for the terminal's own background, which the pane already paints.
    pub background: Option<gpui_kit::Hsla>,
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
    pub strike: bool,
}

/// A visible line: its text, and each run's length in bytes with its look.
pub struct Line {
    pub text: String,
    pub runs: Vec<(usize, Look)>,
}

#[derive(Clone, Default)]
struct Replies(Arc<Mutex<Vec<u8>>>);

impl EventListener for Replies {
    fn send_event(&self, event: Event) {
        if let Event::PtyWrite(text) = event
            && let Ok(mut replies) = self.0.lock()
        {
            replies.extend_from_slice(text.as_bytes());
        }
    }
}

pub struct Screen {
    term: Term<Replies>,
    parser: Processor,
    replies: Replies,
}

impl Screen {
    pub fn new(size: Size) -> Self {
        let replies = Replies::default();
        Self {
            term: Term::new(Config::default(), &size, replies.clone()),
            parser: Processor::new(),
            replies,
        }
    }

    pub fn feed(&mut self, bytes: &[u8]) {
        self.parser.advance(&mut self.term, bytes);
    }

    /// What the program asked the terminal to answer since the last call.
    pub fn replies(&mut self) -> Vec<u8> {
        self.replies
            .0
            .lock()
            .map(|mut r| std::mem::take(&mut *r))
            .unwrap_or_default()
    }

    pub fn resize(&mut self, size: Size) {
        self.term.resize(size);
    }

    pub fn size(&self) -> Size {
        Size {
            cols: self.term.columns() as u16,
            rows: self.term.screen_lines() as u16,
        }
    }

    pub fn mode(&self) -> TermMode {
        *self.term.mode()
    }

    /// Scrolls the view through the scrollback, by lines (positive goes back).
    pub fn scroll(&mut self, lines: i32) {
        self.term.scroll_display(Scroll::Delta(lines));
    }

    /// Back to the live screen, as typing does.
    pub fn scroll_to_bottom(&mut self) {
        self.term.scroll_display(Scroll::Bottom);
    }

    /// The visible lines, with the cursor's cell drawn in `cursor` when it's shown.
    pub fn lines(&self, palette: &Palette, cursor: bool) -> Vec<Line> {
        let content = self.term.renderable_content();
        let colors = content.colors;
        let offset = content.display_offset as i32;
        let show_cursor = cursor && offset == 0 && content.mode.contains(TermMode::SHOW_CURSOR);
        let cursor_at = content.cursor.point;
        let mut lines: Vec<Line> = (0..self.term.screen_lines())
            .map(|_| Line {
                text: String::new(),
                runs: Vec::new(),
            })
            .collect();
        for indexed in content.display_iter {
            let cell = indexed.cell;
            if cell.flags.contains(Flags::WIDE_CHAR_SPACER) {
                continue;
            }
            let Some(line) = lines.get_mut((indexed.point.line.0 + offset) as usize) else {
                continue;
            };
            let mut foreground = palette.color(cell.fg, colors);
            let mut background = match cell.bg {
                Color::Named(NamedColor::Background) => None,
                other => Some(palette.color(other, colors)),
            };
            if cell.flags.contains(Flags::INVERSE) {
                let back = background.unwrap_or(palette.background);
                background = Some(foreground);
                foreground = back;
            }
            if cell.flags.contains(Flags::DIM) {
                foreground = foreground.opacity(0.7);
            }
            if show_cursor && indexed.point == cursor_at {
                background = Some(palette.cursor);
                foreground = palette.background;
            }
            let look = Look {
                foreground,
                background,
                bold: cell.flags.contains(Flags::BOLD),
                italic: cell.flags.contains(Flags::ITALIC),
                underline: cell.flags.intersects(Flags::ALL_UNDERLINES),
                strike: cell.flags.contains(Flags::STRIKEOUT),
            };
            let c = if cell.flags.contains(Flags::HIDDEN) {
                ' '
            } else {
                cell.c
            };
            let start = line.text.len();
            line.text.push(c);
            for extra in cell.zerowidth().into_iter().flatten() {
                line.text.push(*extra);
            }
            let len = line.text.len() - start;
            match line.runs.last_mut() {
                Some((run, last)) if *last == look => *run += len,
                _ => line.runs.push((len, look)),
            }
        }
        lines
    }
}

#[cfg(test)]
#[path = "screen_tests.rs"]
mod tests;
