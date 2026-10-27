//! The theme crossfade: when `set` switches the window from one of our themes to another, the window root draws the
//! old theme's ground over everything and fades it to clear, so the old theme dissolves into the new one. Native
//! views (core/native_views) are OS views above the window's drawing, so the layer doesn't cover them.

use std::time::Instant;

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::*;

/// The last switch between two of our themes.
pub struct LastSwitch {
    pub(super) from: Hsla,
    pub(super) generation: usize,
    at: Instant,
}

impl Global for LastSwitch {}

/// Records a switch away from a theme whose ground was `from`. Each switch starts the fade over.
pub(super) fn record(from: Hsla, cx: &mut App) {
    let generation = cx
        .try_global::<LastSwitch>()
        .map_or(1, |last| last.generation + 1);
    cx.set_global(LastSwitch {
        from,
        generation,
        at: Instant::now(),
    });
}

/// The fading layer, for the window root to draw on top. It takes `motion.duration_normal` on the kit's enter curve,
/// so the new theme shows quickly (the exit curve starts slowly and keeps the window hidden for most of the fade).
/// It leaves the tree once that time has passed, so a window opened later doesn't play it. It has no handlers, so it
/// takes no clicks or hovers (gpui-pre div.rs:2458-2475). Under reduced motion GPUI draws the end state, clear
/// (animation.rs:407-414).
pub fn layer(cx: &App) -> Option<impl IntoElement + use<>> {
    let last = cx.try_global::<LastSwitch>()?;
    let motion = &cx.theme().motion;
    if last.at.elapsed() >= motion.duration_normal {
        return None;
    }
    let easing = motion.easing_enter.clone();
    Some(div().absolute().inset_0().bg(last.from).with_animation(
        ("theme-fade", last.generation),
        Animation::new(motion.duration_normal).with_easing(move |t| easing.sample(t)),
        |layer, t| layer.opacity(1.0 - t),
    ))
}

#[cfg(test)]
mod tests {
    use super::{LastSwitch, layer, record};
    use gpui_kit::component::ActiveTheme as _;
    use gpui_kit::{Hsla, TestAppContext, gpui};
    use std::time::Instant;

    #[gpui::test]
    fn the_layer_is_drawn_only_while_a_fade_runs(cx: &mut TestAppContext) {
        cx.update(|cx| {
            gpui_kit::init(cx);
            assert!(layer(cx).is_none(), "a layer before any switch");
            record(Hsla::default(), cx);
            assert!(layer(cx).is_some(), "no layer right after a switch");
            let over = Instant::now() - cx.theme().motion.duration_normal;
            cx.global_mut::<LastSwitch>().at = over;
            assert!(layer(cx).is_none(), "a layer once the fade is over");
        });
    }
}
