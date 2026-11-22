//! `ContextGauge` is how full the agent's context window is: the kit's progress ring, its arc growing clockwise from
//! 12 o'clock as the window is used, then "N% left". It warms at a quarter left and goes hot at a tenth. The kit eases
//! the arc once when the share changes and never loops. The exact numbers are one hover away. Called by the composer's
//! controls (footer.rs).

use crate::core::theme::text::{TextRole, TextRoleExt as _};

use gpui_kit::component::progress::ProgressCircle;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::SPACE;
use crate::core::ui::three_line_row::tabular;
use crate::features::session::composer::context_meter::ContextMeter;

/// The ring and the 7 between it and its words, the look's `.ctx`.
const RING: Pixels = px(18.);
const RING_GAP: Pixels = px(7.);

/// How full the agent's context window is, at the composer's controls: the ring, "N% left", and the exact numbers
/// on hover.
#[derive(IntoElement)]
pub struct ContextGauge {
    pub meter: ContextMeter,
    /// Names who didn't report a count, rather than showing a zero it never sent.
    pub provider: SharedString,
}

impl RenderOnce for ContextGauge {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let look = Look::of(cx);
        // The ring's colour: the accent, amber at a quarter left, the problem colour at a tenth.
        let color = match (self.meter.hot, self.meter.warm) {
            (true, _) => look.problem,
            (_, true) => look.needs_you,
            _ => look.accent,
        };
        let spent = 100 - self.meter.arc.min(100);
        let left = format!("{}% left", self.meter.left);
        let headline = SharedString::from(self.meter.headline);
        let absent = SharedString::from(format!("not reported by {}", self.provider));
        let rows: [(&'static str, SharedString); 2] = [
            (
                "In",
                self.meter.input.map_or_else(|| absent.clone(), Into::into),
            ),
            ("Out", self.meter.output.map_or(absent, Into::into)),
        ];
        // The look's `.ctx`: the kit's progress ring, then what's left in text-3; the exact numbers on hover.
        h_flex()
            .id("composer:context")
            .flex_none()
            .gap(RING_GAP)
            .mr(SPACE[0])
            .text_role(TextRole::Meta, cx)
            .text_color(look.text_3)
            .font_features(tabular())
            .tooltip(move |window, cx| {
                let (headline, rows) = (headline.clone(), rows.clone());
                Tooltip::element(move |_, cx| popover(headline.clone(), rows.clone(), cx))
                    .build(window, cx)
            })
            .child(
                ProgressCircle::new("composer:context-ring")
                    .value(f32::from(spent))
                    .color(color)
                    .size(RING),
            )
            .child(left)
    }
}

fn popover(
    headline: SharedString,
    rows: [(&'static str, SharedString); 2],
    cx: &App,
) -> impl IntoElement + use<> {
    let muted = cx.theme().muted_foreground;
    v_flex()
        .gap(px(4.))
        .text_role(TextRole::Meta, cx)
        .child(div().font_weight(FontWeight::MEDIUM).child(headline))
        .children(rows.into_iter().map(|(label, value)| {
            h_flex()
                .justify_between()
                .gap(px(16.))
                .child(div().text_color(muted).child(label))
                .child(value)
        }))
}
