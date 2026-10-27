//! Things that open underneath grow in place (look direction #13, #16): a tool call's detail, thinking's text and a
//! fold's work ease their height from 0 to what they hold over `GROW` on the design system's curve, their content
//! fades in from `FADE_AFTER` in, and the rows below slide down as the virtual list measures the row again
//! each frame; shutting runs it back. A chevron turns with it, and an open row sits on a lighter band. It's timed from
//! the toggle (`Kept::toggled_at`), not from when the row was drawn, so a row already open when drawn, or drawn again
//! after scrolling back, is still and draws no frames: only a click moves it. Under Reduce Motion it's at its end at
//! once.

use std::f32::consts::FRAC_PI_2;
use std::time::{Duration, Instant};

use gpui_kit::base::animation::cubic_bezier;
use gpui_kit::component::{ActiveTheme as _, Icon, IconName, Sizable as _};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// How long a row takes to grow open or shut, and a new row to fade in: the one pace of the session view's motion.
pub const GROW: Duration = Duration::from_millis(160);
/// How far into the grow what it holds starts to fade in.
pub const FADE_AFTER: Duration = Duration::from_millis(40);

/// The design system's grow curve, `cubic-bezier(0.16, 1, 0.3, 1)`: from 0 to 1 as `t` goes from 0 to 1.
pub fn ease(t: f32) -> f32 {
    cubic_bezier(0.16, 1.0, 0.3, 1.0)(t)
}

/// How far open it is, 0 shut to 1 open, eased over `duration` from the toggle, `elapsed` ago; `None` for one
/// never toggled. The second value is whether it's still moving.
pub fn progress(open: bool, elapsed: Option<Duration>, duration: Duration) -> (f32, bool) {
    let end = if open { 1.0 } else { 0.0 };
    let Some(elapsed) = elapsed.filter(|elapsed| *elapsed < duration) else {
        return (end, false);
    };
    let t = elapsed.as_secs_f32() / duration.as_secs_f32();
    let eased = ease(t);
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
    let (mut low, mut high) = (0.0, 1.0);
    for _ in 0..32 {
        let mid = (low + high) / 2.0;
        if ease(mid) < covered {
            low = mid;
        } else {
            high = mid;
        }
    }
    now - duration.mul_f32(low)
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

/// How far a growing row's content has faded in, from how far it has grown: nothing until the height is where it is
/// `FADE_AFTER` into the grow, then in with it to the end (about 120 ms). Taken from the height, it turns around with
/// it, so shutting a row partway open never shows content that was still hidden.
pub fn faded(grown: f32) -> f32 {
    let from = ease(FADE_AFTER.as_secs_f32() / GROW.as_secs_f32());
    ((grown - from) / (1.0 - from)).clamp(0.0, 1.0)
}

/// An open row's lighter band (the design system's `raised`), coming in as it grows: `row` keeps its place, the band
/// reaching past it on both sides.
pub fn band(row: Div, grown: f32, cx: &App) -> Div {
    let theme = cx.theme();
    row.px_2()
        .py_1()
        .mx(px(-8.))
        .rounded(theme.radius_lg)
        .when(grown > 0., |row| row.bg(theme.group_box.opacity(grown)))
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
