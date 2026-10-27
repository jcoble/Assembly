//! `ViewState` owns what one native view's place depends on in a frame, and works out whether it moves. It has no
//! window in it, so the rules are unit tested on Linux.

use gpui_kit::*;

pub struct ViewState {
    /// Where the view's host element laid out this frame (its pane's content, below the tab bar, clipped to what's
    /// visible); `None` when the host wasn't laid out, because its tab isn't in front, its dock is closed, another
    /// group is zoomed or its pane was removed.
    laid_out: Option<Bounds<Pixels>>,
    /// Where the view was last put; `None` while hidden.
    placed: Option<Bounds<Pixels>>,
}

impl ViewState {
    /// A new view is hidden until its host lays out.
    pub fn new() -> Self {
        todo!()
    }

    pub fn laid_out(&mut self, _bounds: Bounds<Pixels>) {
        todo!()
    }

    /// Ends the frame: the view's new place (`Some(None)` to hide it) when it differs from the last one, `None` when
    /// nothing changed. `covered` is true while an overlay is open or a drag runs. The next frame starts with no
    /// layout.
    pub fn end_frame(&mut self, _covered: bool) -> Option<Option<Bounds<Pixels>>> {
        todo!()
    }
}
