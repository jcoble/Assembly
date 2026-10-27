//! `ViewState` owns what one native view's place depends on in a frame, and works out whether it moves. It has no
//! window in it, so the rules are unit tested on Linux.

use gpui_kit::*;

#[derive(Default)]
pub struct ViewState {
    /// Where the view's host element laid out this frame (its pane's content, below the tab bar, clipped to what's
    /// visible); `None` when the host wasn't laid out, because its tab isn't in front, its dock is closed, another
    /// group is zoomed or its pane was removed.
    laid_out: Option<Bounds<Pixels>>,
    /// Where the view was last put; `None` while hidden.
    placed: Option<Bounds<Pixels>>,
}

impl ViewState {
    pub fn laid_out(&mut self, bounds: Bounds<Pixels>) {
        self.laid_out = Some(bounds);
    }

    /// Ends the frame: the view's new place (`Some(None)` to hide it) when it differs from the last one, `None` when
    /// nothing changed. `covered` is true while an overlay is open or a drag runs. The next frame starts with no
    /// layout.
    pub fn end_frame(&mut self, covered: bool) -> Option<Option<Bounds<Pixels>>> {
        let laid_out = self.laid_out.take();
        let next = if covered {
            None
        } else {
            laid_out.filter(|bounds| !bounds.is_empty())
        };
        (next != self.placed).then(|| {
            self.placed = next;
            next
        })
    }
}

#[cfg(test)]
mod tests {
    use gpui_kit::{Bounds, point, px, size};

    use super::ViewState;

    fn pane(x: f32) -> Bounds<gpui_kit::Pixels> {
        Bounds::new(point(px(x), px(40.)), size(px(300.), px(200.)))
    }

    // Pins: g0-host#5
    #[test]
    fn a_new_view_stays_hidden_until_its_host_lays_out() {
        let mut view = ViewState::default();
        assert_eq!(view.end_frame(false), None);
    }

    // Pins: g0-host#5, g0-host#7
    #[test]
    fn a_laid_out_view_goes_to_its_pane_and_moves_with_it() {
        let mut view = ViewState::default();
        view.laid_out(pane(0.));
        assert_eq!(view.end_frame(false), Some(Some(pane(0.))));
        view.laid_out(pane(0.));
        assert_eq!(view.end_frame(false), None, "unchanged: no call");
        view.laid_out(pane(50.));
        assert_eq!(view.end_frame(false), Some(Some(pane(50.))));
    }

    // Pins: g0-host#5
    #[test]
    fn a_view_whose_host_did_not_lay_out_is_hidden() {
        let mut view = ViewState::default();
        view.laid_out(pane(0.));
        view.end_frame(false);
        assert_eq!(view.end_frame(false), Some(None));
        assert_eq!(view.end_frame(false), None);
    }

    // Pins: g0-host#6, g0-host#8
    #[test]
    fn a_covered_view_is_hidden_and_comes_back_when_uncovered() {
        let mut view = ViewState::default();
        view.laid_out(pane(0.));
        view.end_frame(false);
        view.laid_out(pane(0.));
        assert_eq!(view.end_frame(true), Some(None));
        view.laid_out(pane(0.));
        assert_eq!(view.end_frame(false), Some(Some(pane(0.))));
    }

    // Pins: g0-host#7
    #[test]
    fn a_host_clipped_away_entirely_hides_the_view() {
        let mut view = ViewState::default();
        view.laid_out(Bounds::new(point(px(0.), px(0.)), size(px(0.), px(200.))));
        assert_eq!(view.end_frame(false), None);
    }
}
