//! The terminal's drawing: each screen line of `TerminalGrid::cells()` as runs of GPUI text in the terminal's
//! monospace font, in the theme's terminal colours (terminal_palette.rs) over its background, with the cursor's cell
//! as a block in the cursor colour. A theme with no terminal colours (the new light ones) takes its ANSI colours from
//! its base colours. The 256-colour table is the usual xterm cube. It also measures the space it has and says how many
//! columns and rows fit.

use alacritty_terminal::vte::ansi::{Color, NamedColor, Rgb};
use gpui_kit::component::{ActiveTheme as _, Theme, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::terminal_grid::GridCell;
use super::terminal_palette::Palette;

/// Fixed-width faces to use when the chosen one isn't on the machine, as the old terminal fell back.
const FIXED_WIDTH: [&str; 4] = [
    "Menlo",
    "DejaVu Sans Mono",
    "Liberation Mono",
    "Noto Sans Mono",
];

/// The chosen face if it's installed, else the first installed fixed-width one. GPUI on its own takes a missing face
/// to its UI fallbacks, which on Linux is the proportional DejaVu Sans, and the face's own fallbacks only cover
/// characters it lacks, so the columns drift.
fn terminal_font(family: &SharedString, window: &Window) -> Font {
    let text = window.text_system();
    let fallbacks = FontFallbacks::from_fonts(FIXED_WIDTH.map(String::from).to_vec());
    let with_fallbacks = |family: SharedString| Font {
        fallbacks: Some(fallbacks.clone()),
        ..gpui_kit::font(family)
    };
    let installed = |font: &Font| {
        (text.get_font_for_id(text.resolve_font(font)))
            .is_some_and(|found| found.family == font.family)
    };
    std::iter::once(family.clone())
        .chain(FIXED_WIDTH.map(SharedString::from))
        .map(with_fallbacks)
        .find(installed)
        .unwrap_or_else(|| with_fallbacks(family.clone()))
}

/// The space for each cell: the font's advance and its line height.
fn cell_size(font: &Font, font_size: Pixels, line_height: Pixels, window: &Window) -> Size<Pixels> {
    let id = window.text_system().resolve_font(font);
    let width = window
        .text_system()
        .advance(id, font_size, 'm')
        .map_or(font_size * 0.6, |advance| advance.width);
    size(width, line_height)
}

/// The grid's lines, filling the pane, and a measure of the space that calls `fitted` with the columns and rows that
/// fit, the grid's bounds in the window and each cell's size, on each paint.
pub fn draw(
    cells: Vec<GridCell>,
    (rows, cursor): (usize, Option<(usize, usize)>),
    text: (SharedString, Pixels, Pixels),
    palette: Option<&Palette>,
    fitted: impl Fn(((usize, usize), Bounds<Pixels>, Size<Pixels>), &mut App) + 'static,
    window: &Window,
    cx: &App,
) -> impl IntoElement {
    let colors = Colors::new(cx.theme(), palette);
    let (family, font_size, line_height) = text;
    let font = terminal_font(&family, window);
    let cell = cell_size(&font, font_size, line_height, window);
    let mut lines: Vec<Vec<GridCell>> = vec![Vec::new(); rows];
    for cell in cells {
        if let Some(line) = lines.get_mut(cell.line) {
            line.push(cell);
        }
    }
    let measure = canvas(
        move |bounds, _, cx| {
            let fit = |space: Pixels, each: Pixels| (space / each).floor().max(1.) as usize;
            let size = (
                fit(bounds.size.width, cell.width),
                fit(bounds.size.height, cell.height),
            );
            cx.defer(move |cx| fitted((size, bounds, cell), cx));
        },
        |_, _, _, _| {},
    )
    .absolute()
    .size_full();
    v_flex()
        .relative()
        .size_full()
        .overflow_hidden()
        .bg(colors.background)
        .font(font)
        .text_size(font_size)
        .line_height(line_height)
        .child(measure)
        .children(lines.into_iter().enumerate().map(|(index, line)| {
            let cursor = cursor
                .filter(|(_, row)| *row == index)
                .map(|(column, _)| column);
            div()
                .flex()
                .flex_none()
                .h(line_height)
                .whitespace_nowrap()
                .children(runs(line, cursor).into_iter().map(|run| {
                    let (mut fg, mut bg) = (colors.of(run.fg), colors.of(run.bg));
                    if run.dim {
                        fg = fg.opacity(0.5);
                    }
                    if run.cursor {
                        (fg, bg) = (colors.background, colors.cursor);
                    }
                    if run.selected {
                        bg = colors.selection;
                        fg = colors.selection_text.unwrap_or(fg);
                    }
                    div()
                        .flex_none()
                        .w(cell.width * run.columns as f32)
                        .text_color(fg)
                        .bg(bg)
                        .when(run.bold, |run| run.font_weight(FontWeight::BOLD))
                        .when(run.italic, |run| run.italic())
                        .when(run.underline, |run| run.underline())
                        .when(run.strike, |run| run.line_through())
                        .when(run.cursor, |run| {
                            run.debug_selector(|| "terminal-cursor".into())
                        })
                        .child(run.text)
                }))
        }))
}

/// Cells next to each other with the same look, as one piece of text.
struct Run {
    text: String,
    /// The terminal columns it covers.
    columns: usize,
    fg: Color,
    bg: Color,
    bold: bool,
    cursor: bool,
    selected: bool,
    italic: bool,
    underline: bool,
    strike: bool,
    /// Faint, drawn at half opacity as the old renderer did.
    dim: bool,
    /// Invisible (SGR 8): blanks, with no marks or lines.
    hidden: bool,
}

impl Run {
    /// The look that decides where one run ends and the next begins.
    fn look(&self) -> (Color, Color, bool, bool, bool, bool, bool, bool, bool) {
        let Run {
            fg,
            bg,
            bold,
            selected,
            italic,
            underline,
            strike,
            dim,
            hidden,
            ..
        } = *self;
        (
            fg, bg, bold, selected, italic, underline, strike, dim, hidden,
        )
    }
}

/// A line's cells as runs, with a blank for each column before a cell that isn't there and the cursor's cell on its
/// own. A wide character covers two columns.
fn runs(line: Vec<GridCell>, cursor: Option<usize>) -> Vec<Run> {
    let mut runs: Vec<Run> = Vec::new();
    let blank = |column| GridCell {
        column,
        line: 0,
        text: ' ',
        fg: Color::Named(NamedColor::Foreground),
        bg: Color::Named(NamedColor::Background),
        bold: false,
        wide: false,
        selected: false,
        italic: false,
        underline: false,
        strike: false,
        dim: false,
        hidden: false,
        marks: Vec::new(),
    };
    let last = line.last().map_or(0, |cell| cell.column + 1);
    let end = last.max(cursor.map_or(0, |column| column + 1));
    let mut cells = line.into_iter().peekable();
    let mut column = 0;
    while column < end {
        let cell = match cells.next_if(|cell| cell.column == column) {
            Some(cell) => cell,
            None => blank(column),
        };
        let columns = if cell.wide { 2 } else { 1 };
        let at_cursor = cursor == Some(column);
        column += columns;
        let piece = Run {
            text: match cell.hidden {
                true => " ".repeat(columns),
                false => std::iter::once(cell.text).chain(cell.marks).collect(),
            },
            columns,
            fg: cell.fg,
            bg: cell.bg,
            bold: cell.bold,
            cursor: at_cursor,
            selected: cell.selected,
            italic: cell.italic,
            underline: cell.underline && !cell.hidden,
            strike: cell.strike && !cell.hidden,
            dim: cell.dim,
            hidden: cell.hidden,
        };
        match runs.last_mut() {
            Some(run) if !at_cursor && !run.cursor && run.look() == piece.look() => {
                run.text.push_str(&piece.text);
                run.columns += columns;
            }
            _ => runs.push(piece),
        }
    }
    runs
}

/// The colours the terminal draws in.
struct Colors {
    background: Hsla,
    foreground: Hsla,
    dim: Hsla,
    cursor: Hsla,
    selection: Hsla,
    /// The selected text's colour; it keeps its own when there's none.
    selection_text: Option<Hsla>,
    ansi: [Hsla; 16],
}

impl Colors {
    /// The palette's colours over the theme's background, or the theme's own colours with no palette.
    fn new(theme: &Theme, palette: Option<&Palette>) -> Self {
        let hex = |value: u32| -> Hsla { gpui_kit::rgb(value).into() };
        let Some(palette) = palette else {
            return Self {
                background: theme.background,
                foreground: theme.foreground,
                dim: theme.muted_foreground,
                cursor: theme.foreground,
                selection: theme.selection,
                selection_text: None,
                ansi: [
                    theme.muted,
                    theme.red,
                    theme.green,
                    theme.yellow,
                    theme.blue,
                    theme.magenta,
                    theme.cyan,
                    theme.foreground,
                    theme.muted_foreground,
                    theme.red_light,
                    theme.green_light,
                    theme.yellow_light,
                    theme.blue_light,
                    theme.magenta_light,
                    theme.cyan_light,
                    theme.foreground,
                ],
            };
        };
        let (selection, opacity) = palette.selection;
        Self {
            background: theme.background,
            foreground: hex(palette.foreground),
            dim: theme.muted_foreground,
            cursor: hex(palette.cursor),
            selection: hex(selection).opacity(opacity),
            selection_text: Some(hex(palette.selection_text)),
            ansi: palette.ansi.map(hex),
        }
    }

    /// A cell's colour.
    fn of(&self, color: Color) -> Hsla {
        match color {
            Color::Spec(rgb) => hsla_of(rgb),
            Color::Indexed(index) if index < 16 => self.ansi[index as usize],
            Color::Indexed(index) => hsla_of(xterm(index)),
            Color::Named(named) => match named {
                NamedColor::Background => self.background,
                NamedColor::DimForeground => self.dim,
                named if (named as usize) < 16 => self.ansi[named as usize],
                _ => self.foreground,
            },
        }
    }
}

/// Colours 16 to 255: the 6×6×6 cube, then 24 greys.
fn xterm(index: u8) -> Rgb {
    let index = index - 16;
    if index >= 216 {
        let grey = 8 + (index - 216) * 10;
        return Rgb {
            r: grey,
            g: grey,
            b: grey,
        };
    }
    let step = |value: u8| if value == 0 { 0 } else { 55 + value * 40 };
    Rgb {
        r: step(index / 36),
        g: step(index / 6 % 6),
        b: step(index % 6),
    }
}

fn hsla_of(Rgb { r, g, b }: Rgb) -> Hsla {
    gpui_kit::rgb((u32::from(r) << 16) | (u32::from(g) << 8) | u32::from(b)).into()
}

#[cfg(test)]
#[path = "terminal_draw_tests.rs"]
mod tests;
