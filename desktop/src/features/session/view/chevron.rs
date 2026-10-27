//! `chevron` is the turning arrow of a fold, a tool call or thinking: right while shut, down while open. Toggled a
//! moment ago it turns with the theme's motion; drawn again later, after scrolling back, it's still.

use std::f32::consts::FRAC_PI_2;

use gpui_kit::base::motion::{Presence, Transition};
use gpui_kit::component::{ActiveTheme as _, Icon, IconName, Sizable as _};
use gpui_kit::*;

pub fn chevron(id: &str, open: bool, moving: bool, window: &mut Window, cx: &mut App) -> Icon {
    let turned = shown(&format!("{id}:chevron"), open, moving, window, cx);
    Icon::new(IconName::ChevronRight)
        .xsmall()
        .rotate(radians(turned * FRAC_PI_2))
}

/// How far in an opening body or chevron is, 0 to 1: eased while `moving`, else where `open` leaves it. The motion is
/// sampled either way, so it's ready to ease from where it is on the next toggle.
pub fn shown(id: &str, open: bool, moving: bool, window: &mut Window, cx: &mut App) -> f32 {
    let duration = cx.theme().motion.duration_normal;
    let sample = Presence::new(SharedString::from(id.to_string()), open)
        .transition(Transition::new(duration))
        .sample(window, cx);
    match (moving, open) {
        (true, _) => sample.progress,
        (false, true) => 1.0,
        (false, false) => 0.0,
    }
}
