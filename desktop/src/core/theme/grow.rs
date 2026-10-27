//! Grow in place's timing (the design system's Motion, look item 13): the one motion token the kit's
//! `cx.theme().motion` doesn't have. A row grows open or shut over `GROW`, and what it reveals fades over `FADE`,
//! starting `FADE_AFTER` in. The curve is the kit's `easing_enter`, the design system's `cubic-bezier(0.16, 1, 0.3, 1)`.

use std::time::Duration;

pub const GROW: Duration = Duration::from_millis(160);
pub const FADE: Duration = Duration::from_millis(120);
pub const FADE_AFTER: Duration = Duration::from_millis(40);
