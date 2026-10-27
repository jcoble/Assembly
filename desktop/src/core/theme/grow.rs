//! Grow in place's timing (the design system's Motion, look item 13): the one motion token the kit's
//! `cx.theme().motion` doesn't have. A row grows open or shut over `GROW` on the design system's
//! `cubic-bezier(0.16, 1, 0.3, 1)` (`ease`; the kit's default `easing_enter` is the same curve), and what it reveals fades in from `FADE_AFTER` in, to the end.

use std::time::Duration;

use gpui_kit::base::animation::cubic_bezier;

pub const GROW: Duration = Duration::from_millis(160);
pub const FADE_AFTER: Duration = Duration::from_millis(40);

/// The grow curve, from 0 to 1 as `t` goes from 0 to 1 through `GROW`.
pub fn ease(t: f32) -> f32 {
    cubic_bezier(0.16, 1.0, 0.3, 1.0)(t)
}

/// How far a growing row's content has faded in, from how far the row has `grown` (0 shut to 1 open): hidden until
/// `FADE_AFTER` into the grow, then in with it to the end (about 120 ms). Taken from the height, it turns around with
/// it, so shutting a row partway open never shows content that was still hidden.
pub fn faded(grown: f32) -> f32 {
    let from = ease(FADE_AFTER.as_secs_f32() / GROW.as_secs_f32());
    ((grown - from) / (1.0 - from)).clamp(0.0, 1.0)
}
