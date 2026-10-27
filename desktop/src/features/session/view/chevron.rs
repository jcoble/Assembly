//! The ease of a fold, a tool call or thinking opening and shutting: `turn` is how far it is, timed from its toggle
//! (`Kept::toggled_at`), and `chevron` the arrow it turns, right while shut, down while open. Timing from the toggle,
//! not from when the row was drawn, a row drawn again after scrolling back is still, and draws no frames.

use std::f32::consts::FRAC_PI_2;
use std::time::{Duration, Instant};

use gpui_kit::component::{ActiveTheme as _, Icon, IconName, Sizable as _};
use gpui_kit::*;

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

/// `progress` at this frame, over the theme's normal duration; asks for the next frame while it moves. With Reduce
/// Motion on it's at its end at once.
pub fn turn(open: bool, toggled_at: Option<Instant>, window: &mut Window, cx: &App) -> f32 {
    let toggled_at = toggled_at.filter(|_| !cx.reduce_motion());
    let duration = cx.theme().motion.duration_normal;
    let (turned, moving) = progress(open, toggled_at.map(|at| at.elapsed()), duration);
    if moving {
        window.request_animation_frame();
    }
    turned
}

pub fn chevron(turned: f32) -> Icon {
    Icon::new(IconName::ChevronRight)
        .xsmall()
        .rotate(radians(turned * FRAC_PI_2))
}

#[cfg(test)]
#[path = "chevron_tests.rs"]
mod tests;
