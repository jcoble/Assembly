//! Where the terminal's scrollbar draws and what a press on it means, from the old renderer's numbers
//! (ghostty-web.js `renderScrollbar`, `handleMouseDown`, `processScrollbarDrag`): a bar 8 px wide, 4 px from the grid's
//! right edge, its track 4 px in from the top and bottom, and a thumb at least 20 px tall. Pixels here are from the
//! grid's top left.

use std::time::Duration;

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::*;

/// How long the bar stays after the last scroll, and how long it takes to fade in or out.
pub const HIDE_AFTER: Duration = Duration::from_millis(1500);
pub const FADE: Duration = Duration::from_millis(200);

const WIDTH: Pixels = px(8.);
const RIGHT: Pixels = px(4.);
const PADDING: Pixels = px(4.);
const MIN_THUMB: Pixels = px(20.);

/// The thumb's top and height, in a grid `height` tall, with `rows` on screen, `history` lines above them and the view
/// `offset` lines back.
pub fn thumb(rows: usize, history: usize, offset: usize, height: Pixels) -> (Pixels, Pixels) {
    let track = height - PADDING * 2.;
    let size = (track * (rows as f32 / (history + rows).max(1) as f32)).max(MIN_THUMB);
    let back = offset as f32 / history.max(1) as f32;
    (PADDING + (track - size) * (1. - back), size)
}

/// The offset a position on the track stands for, stopping at the track's ends.
pub fn offset_at(y: Pixels, history: usize, height: Pixels) -> usize {
    let back = 1. - (y - PADDING) / (height - PADDING * 2.);
    (back * history as f32).round().clamp(0., history as f32) as usize
}

/// The offset a thumb drag `moved` down from where it started at `from` stands for.
pub fn dragged(
    (rows, history, from): (usize, usize, usize),
    moved: Pixels,
    height: Pixels,
) -> usize {
    let (_, size) = thumb(rows, history, from, height);
    let lines = (-moved / (height - PADDING * 2. - size) * history as f32).round();
    (from as f32 + lines).clamp(0., history as f32) as usize
}

/// Whether `x` is on the bar's column, in a grid `width` wide.
pub fn on_bar(x: Pixels, width: Pixels) -> bool {
    let left = width - RIGHT - WIDTH;
    x >= left && x <= left + WIDTH
}

/// The bar over a grid of `area`, faded to `opacity`: the track, and the thumb, brighter while scrolled back.
pub fn bar(
    (rows, history, offset): (usize, usize, usize),
    area: Size<Pixels>,
    opacity: f32,
    cx: &App,
) -> Div {
    let theme = cx.theme();
    let (top, size) = thumb(rows, history, offset, area.height);
    let thumb_color = match offset > 0 {
        true => theme.scrollbar_thumb_hover,
        false => theme.scrollbar_thumb,
    };
    div()
        .absolute()
        .left(area.width - RIGHT - WIDTH)
        .top(PADDING)
        .w(WIDTH)
        .h(area.height - PADDING * 2.)
        .bg(theme.scrollbar)
        .opacity(opacity)
        .child(
            div()
                .absolute()
                .top(top - PADDING)
                .w_full()
                .h(size)
                .bg(thumb_color),
        )
}
