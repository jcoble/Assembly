//! `Screen` owns one terminal's emulation in the window: the bytes go into Ghostty's VT parser and grid
//! (libghostty-vt, the only code that reads escape sequences), and it lays the visible grid out as styled lines for
//! the pane to draw. What the program asks the terminal to answer (a cursor position report, device attributes)
//! comes back as `replies`, to write to it. Replaces ghosttyFactory.ts's terminal (ghostty-web, the same parser
//! built for the page) and its fit.

use std::cell::RefCell;
use std::rc::Rc;

use libghostty_vt::render::{CellIterator, RowIterator};
use libghostty_vt::screen::CellWide;
use libghostty_vt::style::{self, Underline};
use libghostty_vt::terminal::{
    ConformanceLevel, DeviceAttributeFeature, DeviceAttributes, DeviceType, Mode,
    PrimaryDeviceAttributes, ScrollViewport, SecondaryDeviceAttributes,
};
use libghostty_vt::{RenderState, Terminal, TerminalOptions};

use super::palette::{Palette, hsla, rgb};

/// Lines kept above the screen, for scrolling back.
const SCROLLBACK: usize = 10_000;

/// A size in cells, as the pane measured it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Size {
    pub cols: u16,
    pub rows: u16,
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

pub struct Screen {
    term: Terminal<'static, 'static>,
    state: RenderState<'static>,
    rows: RowIterator<'static>,
    cells: CellIterator<'static>,
    replies: Rc<RefCell<Vec<u8>>>,
    /// The palette Ghostty has, so a theme change is passed on once.
    palette: Option<Palette>,
}

impl Screen {
    pub fn new(size: Size) -> Self {
        let replies = Rc::new(RefCell::new(Vec::new()));
        let mut term = Terminal::new(TerminalOptions {
            cols: size.cols,
            rows: size.rows,
            max_scrollback: SCROLLBACK,
        })
        .expect("a terminal of a measured size starts");
        let written = replies.clone();
        term.on_pty_write(move |_, data| written.borrow_mut().extend_from_slice(data))
            .and_then(|term| {
                // As Ghostty answers: a VT220 with ANSI color.
                term.on_device_attributes(|_| {
                    Some(DeviceAttributes {
                        primary: PrimaryDeviceAttributes::new(
                            ConformanceLevel::VT220,
                            &[DeviceAttributeFeature::ANSI_COLOR],
                        ),
                        secondary: SecondaryDeviceAttributes {
                            device_type: DeviceType::VT220,
                            firmware_version: 0,
                            rom_cartridge: 0,
                        },
                        tertiary: Default::default(),
                    })
                })
            })
            .expect("the terminal takes its answers");
        Self {
            term,
            state: RenderState::new().expect("a render state starts"),
            rows: RowIterator::new().expect("a row iterator starts"),
            cells: CellIterator::new().expect("a cell iterator starts"),
            replies,
            palette: None,
        }
    }

    pub fn feed(&mut self, bytes: &[u8]) {
        self.term.vt_write(bytes);
    }

    /// What the program asked the terminal to answer since the last call.
    pub fn replies(&mut self) -> Vec<u8> {
        self.replies.take()
    }

    pub fn resize(&mut self, size: Size) {
        if let Err(error) = self.term.resize(size.cols, size.rows, 0, 0) {
            eprintln!("terminal: resizing to {size:?} failed: {error:?}");
        }
    }

    pub fn size(&self) -> Size {
        Size {
            cols: self.term.cols().unwrap_or(0),
            rows: self.term.rows().unwrap_or(0),
        }
    }

    /// Whether the program wants the cursor keys' application form.
    pub fn app_cursor(&self) -> bool {
        self.term.mode(Mode::DECCKM).unwrap_or(false)
    }

    /// Whether the program wants pasted text marked as a paste.
    pub fn bracketed_paste(&self) -> bool {
        self.term.mode(Mode::BRACKETED_PASTE).unwrap_or(false)
    }

    /// Scrolls the view through the scrollback, by lines (positive goes back).
    pub fn scroll(&mut self, lines: i32) {
        self.term
            .scroll_viewport(ScrollViewport::Delta(-(lines as isize)));
    }

    /// Back to the live screen, as typing does.
    pub fn scroll_to_bottom(&mut self) {
        self.term.scroll_viewport(ScrollViewport::Bottom);
    }

    /// The visible lines, with the cursor's cell drawn in `cursor` when it's shown.
    pub fn lines(&mut self, palette: &Palette, cursor: bool) -> Vec<Line> {
        if self.palette.as_ref() != Some(palette) {
            self.use_palette(palette);
        }
        let Ok(snapshot) = self.state.update(&self.term) else {
            return Vec::new();
        };
        let foreground = snapshot
            .colors()
            .map(|colors| hsla(colors.foreground))
            .unwrap_or(palette.foreground);
        // Off when the view is scrolled back, since the cursor isn't in it then.
        let cursor_at = if cursor && snapshot.cursor_visible().unwrap_or(false) {
            snapshot.cursor_viewport().ok().flatten()
        } else {
            None
        };
        let mut lines = Vec::new();
        let Ok(mut rows) = self.rows.update(&snapshot) else {
            return lines;
        };
        let mut grapheme = String::new();
        while let Some(row) = rows.next() {
            let y = lines.len() as u16;
            let mut line = Line {
                text: String::new(),
                runs: Vec::new(),
            };
            if let Ok(mut cells) = self.cells.update(row) {
                let mut x = 0u16;
                while let Some(cell) = cells.next() {
                    let at = x;
                    x += 1;
                    let wide = cell.raw_cell().and_then(|raw| raw.wide());
                    if matches!(wide, Ok(CellWide::SpacerTail)) {
                        continue;
                    }
                    let style = cell.style().unwrap_or_default();
                    let mut fore = cell
                        .fg_color()
                        .ok()
                        .flatten()
                        .map(hsla)
                        .unwrap_or(foreground);
                    let mut back = cell.bg_color().ok().flatten().map(hsla);
                    if style.inverse {
                        let under = back.unwrap_or(palette.background);
                        back = Some(fore);
                        fore = under;
                    }
                    if style.faint {
                        fore = fore.opacity(0.7);
                    }
                    if cursor_at.is_some_and(|c| c.x == at && c.y == y) {
                        back = Some(palette.cursor);
                        fore = palette.background;
                    }
                    let look = Look {
                        foreground: fore,
                        background: back,
                        bold: style.bold,
                        italic: style.italic,
                        underline: style.underline != Underline::None,
                        strike: style.strikethrough,
                    };
                    grapheme.clear();
                    if style.invisible
                        || cell.graphemes_utf8(&mut grapheme).is_err()
                        || grapheme.is_empty()
                    {
                        grapheme.clear();
                        grapheme.push(' ');
                    }
                    line.text.push_str(&grapheme);
                    match line.runs.last_mut() {
                        Some((run, last)) if *last == look => *run += grapheme.len(),
                        _ => line.runs.push((grapheme.len(), look)),
                    }
                }
            }
            lines.push(line);
        }
        lines
    }

    fn use_palette(&mut self, palette: &Palette) {
        let set = self
            .term
            .set_default_color_palette(Some(style::Palette(palette.colors)))
            .and_then(|term| {
                term.set_default_fg_color(Some(rgb(palette.foreground, palette.background)))
            })
            .and_then(|term| {
                term.set_default_bg_color(Some(rgb(palette.background, palette.background)))
            })
            .and_then(|term| {
                term.set_default_cursor_color(Some(rgb(palette.cursor, palette.background)))
            });
        if let Err(error) = set {
            eprintln!("terminal: the theme's colors weren't taken: {error:?}");
        }
        self.palette = Some(palette.clone());
    }
}

#[cfg(test)]
#[path = "screen_tests.rs"]
mod tests;
