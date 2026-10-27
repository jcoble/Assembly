//! `ViewState` owns what one native view's place depends on, and works out whether it moves. It has no window in it,
//! so the rules are unit tested on Linux.

use gpui_kit::*;

#[derive(Default)]
pub struct ViewState {
    /// Where the view's host last laid out (its pane's content, below the tab bar, clipped to what's visible). It's
    /// kept while the host isn't laid out again, because the dock reuses an unchanged pane's last layout without
    /// running its host; `forget` clears it when the layout changes, so a pane that's no longer drawn hides its view.
    bounds: Option<Bounds<Pixels>>,
    /// Where the view was last put; `None` while hidden.
    placed: Option<Bounds<Pixels>>,
}

impl ViewState {
    pub fn laid_out(&mut self, bounds: Bounds<Pixels>) {
        self.bounds = Some(bounds);
    }

    /// The dock's layout changed: the view hides unless its host lays out again in the next frame.
    pub fn forget(&mut self) {
        self.bounds = None;
    }

    /// Ends the frame: the view's new place (`Some(None)` to hide it) when it differs from the last one, `None` when
    /// nothing changed. `covered` is true while an overlay is open or a drag runs.
    pub fn end_frame(&mut self, covered: bool) -> Option<Option<Bounds<Pixels>>> {
        let next = if covered {
            None
        } else {
            self.bounds.filter(|bounds| !bounds.is_empty())
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
    fn a_reused_pane_keeps_its_view_where_it_was() {
        let mut view = ViewState::default();
        view.laid_out(pane(0.));
        view.end_frame(false);
        assert_eq!(view.end_frame(false), None);
    }

    // Pins: g0-host#5
    #[test]
    fn after_a_layout_change_a_view_whose_host_did_not_lay_out_is_hidden() {
        let mut view = ViewState::default();
        view.laid_out(pane(0.));
        view.end_frame(false);
        view.forget();
        assert_eq!(view.end_frame(false), Some(None));
    }

    // Pins: g0-host#5
    #[test]
    fn after_a_layout_change_a_view_whose_host_laid_out_stays() {
        let mut view = ViewState::default();
        view.laid_out(pane(0.));
        view.end_frame(false);
        view.forget();
        view.laid_out(pane(0.));
        assert_eq!(view.end_frame(false), None);
    }

    // Pins: g0-host#6, g0-host#8
    #[test]
    fn a_covered_view_is_hidden_and_comes_back_when_uncovered() {
        let mut view = ViewState::default();
        view.laid_out(pane(0.));
        view.end_frame(false);
        assert_eq!(view.end_frame(true), Some(None));
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
