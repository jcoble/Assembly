//! Where the terminal's scrollbar draws, from the old renderer's numbers. Stub until Task 7.

use gpui_kit::{Pixels, px};

/// The thumb's top and height, in a pane `height` tall, with `rows` on screen, `history` lines above them and the view
/// `offset` lines back.
#[allow(dead_code)]
pub fn thumb(_rows: usize, _history: usize, _offset: usize, _height: Pixels) -> (Pixels, Pixels) {
    (px(0.), px(0.))
}

/// The offset a position on the track stands for.
#[allow(dead_code)]
pub fn offset_at(_y: Pixels, _history: usize, _height: Pixels) -> usize {
    0
}
