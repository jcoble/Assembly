//! Grow in place's timing (the design system's Motion, look item 13): the one motion token the kit's
//! `cx.theme().motion` doesn't have. A row grows open or shut over `GROW` on the kit's `easing_enter`, the design
//! system's `cubic-bezier(0.16, 1, 0.3, 1)`, and what it reveals fades in from `FADE_AFTER` in, to the end.

use std::time::Duration;

use gpui_kit::base::animation::cubic_bezier;

pub const GROW: Duration = Duration::from_millis(160);
pub const FADE_AFTER: Duration = Duration::from_millis(40);

/// How far a growing row's content has faded in, from how far the row has `grown` (0 shut to 1 open): hidden until
/// `FADE_AFTER` into the grow, then in with it to the end (about 120 ms). Taken from the height, it turns around with
/// it, so shutting a row partway open never shows content that was still hidden.
pub fn faded(grown: f32) -> f32 {
    let from = cubic_bezier(0.16, 1.0, 0.3, 1.0)(FADE_AFTER.as_secs_f32() / GROW.as_secs_f32());
    ((grown - from) / (1.0 - from)).clamp(0.0, 1.0)
}
