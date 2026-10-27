//! Things that open underneath grow in place (look direction #16): a tool call's detail, thinking's text and a fold's
//! work ease their height from 0 to what they hold over `GROW` (ease-out), their content fades in over `FADE`, and
//! the rows below slide down as the virtual list measures the row again each frame; shutting runs it back. A chevron
//! turns with it. It's timed from the toggle (`Kept::toggled_at`), not from when the row was drawn, so a row drawn
//! again after scrolling back is still and draws no frames. Under Reduce Motion it's at its end at once.

use std::f32::consts::FRAC_PI_2;
use std::time::{Duration, Instant};

use gpui_kit::component::{Icon, IconName, Sizable as _};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// How long a row takes to grow open or shut, and a new row to fade in: the one pace of the session view's motion.
pub const GROW: Duration = Duration::from_millis(160);
/// How long what a growing row holds takes to fade in.
pub const FADE: Duration = Duration::from_millis(120);

/// How far open it is, 0 shut to 1 open, eased out over `duration` from the toggle, `elapsed` ago; `None` for one
/// never toggled. The second value is whether it's still moving.
pub fn progress(open: bool, elapsed: Option<Duration>, duration: Duration) -> (f32, bool) {
    let end = if open { 1.0 } else { 0.0 };
    let Some(elapsed) = elapsed.filter(|elapsed| *elapsed < duration) else {
        return (end, false);
    };
    let t = elapsed.as_secs_f32() / duration.as_secs_f32();
    let eased = 1.0 - (1.0 - t).powi(2);
    (if open { eased } else { 1.0 - eased }, true)
}

/// When a toggle to `open` at `now` counts as started, `last` being the one before: at `now`, unless that one is still
/// easing, when it's set back so the ease turns around from where it is instead of jumping to the far end.
pub fn toggled(open: bool, last: Option<Instant>, now: Instant, duration: Duration) -> Instant {
    let (at, moving) = progress(!open, last.map(|last| now.duration_since(last)), duration);
    if !moving {
        return now;
    }
    // How far toward its new end it already is, and the time into the ease that is.
    let covered = if open { at } else { 1.0 - at };
    now - duration.mul_f32(1.0 - (1.0 - covered).sqrt())
}

/// How far it has grown at this frame, over `GROW`, and whether it's still moving; asks for the next frame while it
/// moves. With Reduce Motion on it's at its end at once.
pub fn turn(open: bool, toggled_at: Option<Instant>, window: &mut Window, cx: &App) -> (f32, bool) {
    let toggled_at = toggled_at.filter(|_| !cx.reduce_motion());
    let (grown, moving) = progress(open, toggled_at.map(|at| at.elapsed()), GROW);
    if moving {
        window.request_animation_frame();
    }
    (grown, moving)
}

/// How far a growing row's content has faded in, over `FADE`: it fades out as it shuts.
pub fn faded(open: bool, toggled_at: Option<Instant>, cx: &App) -> f32 {
    let toggled_at = toggled_at.filter(|_| !cx.reduce_motion());
    progress(open, toggled_at.map(|at| at.elapsed()), FADE).0
}

/// `body` growing in place: while it moves, cut to `grown` of the height it was last measured at (0 before the first
/// measure, which is where it grows from), and faded; still, it's drawn at its own height. Its height is measured on
/// every draw, so a body that shuts starts from where it is.
pub fn grow(
    id: SharedString,
    (grown, moving): (f32, bool),
    open: bool,
    faded: f32,
    body: impl IntoElement,
    window: &mut Window,
    cx: &mut App,
) -> AnyElement {
    let height = window.use_keyed_state(id, cx, |_, _| px(0.));
    let measured = height.clone();
    let inner = div().flex_none().w_full().opacity(faded).child(body);
    div()
        .w_full()
        // Not measured yet (just drawn), a body shutting is drawn whole for this frame rather than from 0.
        .when(moving && (open || *height.read(cx) > px(0.)), |outer| {
            outer.h(*height.read(cx) * grown).overflow_hidden()
        })
        .on_children_prepainted(move |bounds, _, cx| {
            if let Some(bounds) = bounds.first() {
                measured.update(cx, |height, _| *height = bounds.size.height);
            }
        })
        .child(inner)
        .into_any_element()
}

pub fn chevron(turned: f32) -> Icon {
    Icon::new(IconName::ChevronRight)
        .xsmall()
        .rotate(radians(turned * FRAC_PI_2))
}

#[cfg(test)]
#[path = "grow_tests.rs"]
mod tests;
