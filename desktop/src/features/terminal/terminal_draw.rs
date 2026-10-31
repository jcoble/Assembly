//! The terminal's drawing: each screen line of `TerminalGrid::cells()` as runs of GPUI text in the editor's monospace
//! font, in the theme's colours, with the cursor's cell drawn inverted. The kit theme has no terminal palette yet
//! (implementation-plan.md, the themes' terminal palettes come with W4's later part), so the ANSI colours come from
//! its base colours, and the 256-colour table from the usual xterm cube. It also measures the space it has and says
//! how many columns and rows fit.

use alacritty_terminal::vte::ansi::{Color, NamedColor, Rgb};
use gpui_kit::component::{ActiveTheme as _, Theme, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::terminal_grid::GridCell;

/// The space for each cell: the font's advance and its line height.
pub fn cell_size(
    (font, font_size, line_height): &(SharedString, Pixels, Pixels),
    window: &Window,
) -> Size<Pixels> {
    let id = window
        .text_system()
        .resolve_font(&gpui_kit::font(font.clone()));
    let width = window
        .text_system()
        .advance(id, *font_size, 'm')
        .map_or(*font_size * 0.6, |advance| advance.width);
    size(width, *line_height)
}

/// The grid's lines, filling the pane, and a measure of the space that calls `fitted` with the columns and rows that
/// fit, where the grid starts in the window and each cell's size, on each paint.
pub fn draw(
    cells: Vec<GridCell>,
    (rows, cursor): (usize, Option<(usize, usize)>),
    text: (SharedString, Pixels, Pixels),
    fitted: impl Fn(((usize, usize), Point<Pixels>, Size<Pixels>), &mut App) + 'static,
    window: &Window,
    cx: &App,
) -> impl IntoElement {
    let theme = cx.theme();
    let cell = cell_size(&text, window);
    let (font, font_size, line_height) = text;
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
            let origin = bounds.origin;
            cx.defer(move |cx| fitted((size, origin, cell), cx));
        },
        |_, _, _, _| {},
    )
    .absolute()
    .size_full();
    v_flex()
        .relative()
        .size_full()
        .overflow_hidden()
        .bg(theme.background)
        .font_family(font)
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
                    let (mut fg, mut bg) = (color(run.fg, theme), color(run.bg, theme));
                    if run.cursor {
                        (fg, bg) = (bg, fg);
                    }
                    if run.selected {
                        bg = theme.selection;
                    }
                    div()
                        .flex_none()
                        .w(cell.width * run.columns as f32)
                        .text_color(fg)
                        .bg(bg)
                        .when(run.bold, |run| run.font_weight(FontWeight::BOLD))
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
        match runs.last_mut() {
            Some(run)
                if !at_cursor
                    && !run.cursor
                    && (run.fg, run.bg, run.bold, run.selected)
                        == (cell.fg, cell.bg, cell.bold, cell.selected) =>
            {
                run.text.push(cell.text);
                run.columns += columns;
            }
            _ => runs.push(Run {
                text: cell.text.to_string(),
                columns,
                fg: cell.fg,
                bg: cell.bg,
                bold: cell.bold,
                cursor: at_cursor,
                selected: cell.selected,
            }),
        }
    }
    runs
}

/// A cell's colour in the theme.
fn color(color: Color, theme: &Theme) -> Hsla {
    match color {
        Color::Spec(rgb) => hsla_of(rgb),
        Color::Indexed(index) if index < 16 => ansi(index, theme),
        Color::Indexed(index) => hsla_of(xterm(index)),
        Color::Named(named) => match named {
            NamedColor::Foreground | NamedColor::BrightForeground | NamedColor::Cursor => {
                theme.foreground
            }
            NamedColor::Background => theme.background,
            NamedColor::DimForeground => theme.muted_foreground,
            named if (named as usize) < 16 => ansi(named as u8, theme),
            _ => theme.foreground,
        },
    }
}

/// The 16 ANSI colours from the theme's base colours.
fn ansi(index: u8, theme: &Theme) -> Hsla {
    match index {
        0 => theme.muted,
        1 => theme.red,
        2 => theme.green,
        3 => theme.yellow,
        4 => theme.blue,
        5 => theme.magenta,
        6 => theme.cyan,
        7 => theme.foreground,
        8 => theme.muted_foreground,
        9 => theme.red_light,
        10 => theme.green_light,
        11 => theme.yellow_light,
        12 => theme.blue_light,
        13 => theme.magenta_light,
        14 => theme.cyan_light,
        _ => theme.foreground,
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
