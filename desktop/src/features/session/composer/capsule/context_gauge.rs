//! `ContextGauge` is how full the agent's context window is, as a small pie: a slice inside a faint outline grows
//! clockwise from 12 o'clock as the window is used, so a fresh session shows a thin slice and one near its limit a nearly
//! full disc. It warms at a quarter left and goes hot at a tenth, and never animates: a moving arc reads as a spinner.
//! The exact numbers are one hover away.

use std::f32::consts::TAU;

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::*;

use crate::features::session::composer::context_meter::ContextMeter;

const CELL: Pixels = px(28.);
const DISC: Pixels = px(18.);
/// Fractions of the disc's radius: the outline sits on its edge and the slice inside it.
const OUTLINE: f32 = 0.9;
const SLICE: f32 = 0.7;

#[derive(IntoElement)]
pub struct ContextGauge {
    pub meter: ContextMeter,
}

impl RenderOnce for ContextGauge {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let color = match (self.meter.hot, self.meter.warm) {
            (true, _) => theme.danger,
            (_, true) => theme.warning,
            _ => theme.muted_foreground,
        };
        let spent = 100 - self.meter.arc.min(100);
        let headline = SharedString::from(self.meter.headline);
        div()
            .id("composer:context")
            .flex()
            .flex_none()
            .items_center()
            .justify_center()
            .size(CELL)
            .mr(px(6.))
            .tooltip(move |window, cx| Tooltip::new(headline.clone()).build(window, cx))
            .child(
                canvas(
                    |_, _, _| {},
                    move |bounds, _, window, _| paint(bounds, spent, color, window),
                )
                .size(DISC),
            )
    }
}

fn paint(bounds: Bounds<Pixels>, spent: u8, color: Hsla, window: &mut Window) {
    let center = bounds.center();
    let radius = bounds.size.width / 2.;
    let on = |edge: Pixels, turn: f32| {
        let (sin, cos) = (turn * TAU).sin_cos();
        point(center.x + edge * sin, center.y - edge * cos)
    };
    let round = |path: &mut PathBuilder, edge: Pixels| {
        let radii = point(edge, edge);
        path.move_to(on(edge, 0.));
        path.arc_to(radii, px(0.), false, true, on(edge, 0.5));
        path.arc_to(radii, px(0.), false, true, on(edge, 0.));
        path.close();
    };
    let mut track = PathBuilder::stroke(px(1.35));
    round(&mut track, radius * OUTLINE);
    if let Ok(track) = track.build() {
        window.paint_path(track, color.opacity(0.45));
    }
    if spent == 0 {
        return;
    }
    let edge = radius * SLICE;
    let mut slice = PathBuilder::fill();
    if spent >= 100 {
        round(&mut slice, edge);
    } else {
        let turn = f32::from(spent) / 100.;
        slice.move_to(center);
        slice.line_to(on(edge, 0.));
        slice.arc_to(point(edge, edge), px(0.), turn > 0.5, true, on(edge, turn));
        slice.close();
    }
    if let Ok(slice) = slice.build() {
        window.paint_path(slice, color);
    }
}
