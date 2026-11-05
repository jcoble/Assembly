//! The Terminal tab's scrollbar (spec 6): it shows while scrolling, only with history to scroll back to, fades in and
//! out over `FADE` and hides `HIDE_AFTER` the last scroll, as the old renderer's. One task does the fading and the wait
//! and ends once hidden. A press on the bar's column drags the thumb, or jumps the view to a point on the track; it
//! neither selects nor reaches a program holding the mouse, nor does its release, and the bar stays shown while held.

use std::time::Duration;

use gpui_kit::*;

use super::TerminalPane;
use crate::features::terminal::terminal_scrollbar::{
    FADE, HIDE_AFTER, bar, dragged, offset_at, on_bar, thumb,
};

/// Steps in each fade.
const FADE_STEPS: u32 = 10;

/// Fades the bar from one opacity to another over `FADE`, and says whether the pane is still there.
async fn fade(pane: &WeakEntity<TerminalPane>, cx: &mut AsyncApp, from: f32, to: f32) -> bool {
    for done in 1..=FADE_STEPS {
        cx.background_executor().timer(FADE / FADE_STEPS).await;
        let opacity = match done {
            FADE_STEPS => to,
            _ => from + (to - from) * done as f32 / FADE_STEPS as f32,
        };
        let faded = pane.update(cx, |pane, cx| {
            pane.scrollbar_opacity = opacity;
            cx.notify();
        });
        if faded.is_err() {
            return false;
        }
    }
    true
}

impl TerminalPane {
    /// The view scrolled: a hidden bar fades in, one that shows (fading out too) is fully shown at once, as the old
    /// renderer's, and the wait to hide it starts over.
    pub(super) fn show_scrollbar(&mut self, cx: &mut Context<Self>) {
        if self.grid.history_size() == 0 {
            return;
        }
        let fading_in = self.scrollbar_opacity == 0. && self._scrollbar.is_none();
        if !fading_in {
            self.scrollbar_opacity = 1.;
            cx.notify();
        }
        self._scrollbar = Some(cx.spawn(async move |pane, cx| {
            if fading_in && !fade(&pane, cx, 0., 1.).await {
                return;
            }
            let waited = if fading_in { FADE } else { Duration::ZERO };
            cx.background_executor().timer(HIDE_AFTER - waited).await;
            // Held by a drag: it stays, and its release shows it again.
            let held = pane.update(cx, |pane, _| pane.dragging_bar.is_some());
            if held.unwrap_or(true) || fade(&pane, cx, 1., 0.).await {
                pane.update(cx, |pane, _| pane._scrollbar = None).ok();
            }
        }));
    }

    /// The grid's rows and columns as drawn, from its top left.
    fn grid_area(&self) -> Size<Pixels> {
        size(
            self.cell.width * self.size.0 as f32,
            self.cell.height * self.size.1 as f32,
        )
    }

    /// A left press: on the bar's column, with history, it takes the thumb, or jumps there when off the thumb, and
    /// says so.
    pub(super) fn press_bar(&mut self, position: Point<Pixels>, cx: &mut Context<Self>) -> bool {
        let history = self.grid.history_size();
        let (area, at) = (self.grid_area(), position - self.origin);
        if history == 0 || !on_bar(at.x, area.width) || at.y < px(0.) || at.y > area.height {
            return false;
        }
        let (top, size) = thumb(
            self.size.1,
            history,
            self.grid.display_offset(),
            area.height,
        );
        if at.y < top || at.y > top + size {
            self.scroll_bar_to(offset_at(at.y, history, area.height), cx);
        } else {
            self.dragging_bar = Some((at.y, self.grid.display_offset()));
        }
        self.bar_pressed = true;
        cx.notify();
        true
    }

    fn scroll_bar_to(&mut self, offset: usize, cx: &mut Context<Self>) {
        if offset != self.grid.display_offset() {
            self.grid.scroll_to_offset(offset);
            self.unhover();
            self.show_scrollbar(cx);
            cx.notify();
        }
    }

    /// The bar is pressed: a held thumb follows the mouse, and a release lets go of it. Taken before the pane's own
    /// listeners, so none of it selects or reaches a program, past the pane's edge too. A release in the same frame as
    /// the press, before these are painted, reaches the pane's `mouse_up`, which lets go the same way.
    pub(super) fn bar_listeners(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let this = cx.entity().downgrade();
        canvas(
            |_, _, _| (),
            move |_, _, window, _| {
                let moved = this.clone();
                window.on_mouse_event(move |event: &MouseMoveEvent, phase, _, cx| {
                    if phase.capture() && event.pressed_button == Some(MouseButton::Left) {
                        let y = event.position.y;
                        moved.update(cx, |pane, cx| pane.drag_bar(y, cx)).ok();
                        cx.stop_propagation();
                    }
                });
                let released = this.clone();
                window.on_mouse_event(move |event: &MouseUpEvent, phase, _, cx| {
                    if phase.capture() && event.button == MouseButton::Left {
                        released.update(cx, |pane, cx| pane.release_bar(cx)).ok();
                        cx.stop_propagation();
                    }
                });
            },
        )
        .absolute()
        .size_0()
    }

    fn drag_bar(&mut self, y: Pixels, cx: &mut Context<Self>) {
        let Some((from_y, from)) = self.dragging_bar else {
            return;
        };
        let history = self.grid.history_size();
        let moved = y - self.origin.y - from_y;
        let height = self.grid_area().height;
        self.scroll_bar_to(dragged((self.size.1, history, from), moved, height), cx);
    }

    /// Lets go of a press on the bar, and says whether there was one. As the old renderer, only a thumb drag of a
    /// bar that shows starts its wait again.
    pub(super) fn release_bar(&mut self, cx: &mut Context<Self>) -> bool {
        let showing = self.scrollbar_opacity > 0. || self._scrollbar.is_some();
        if self.dragging_bar.take().is_some() && showing {
            self.show_scrollbar(cx);
        }
        let pressed = std::mem::take(&mut self.bar_pressed);
        if pressed {
            cx.notify();
        }
        pressed
    }

    /// The bar over the grid while it shows.
    pub(super) fn scrollbar(&self, cx: &App) -> Option<Div> {
        let history = self.grid.history_size();
        (self.scrollbar_opacity > 0. && history > 0).then(|| {
            let lines = (self.size.1, history, self.grid.display_offset());
            bar(lines, self.grid_area(), self.scrollbar_opacity, cx)
        })
    }
}
