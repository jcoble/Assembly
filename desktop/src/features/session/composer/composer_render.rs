//! How the composer draws, to the approved look (the mockup canvas's board 4): one box on the raised ground with the
//! design system's radius-xl corners, the draft on top and a footer under it with the context ring, Stop while a turn
//! runs and the round Send, which is the one solid button.

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::Textarea;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::Composer;
use crate::features::session::composer::context_ring::ContextRing;

/// The design system's `radius-xl`, the composer's corners.
const BOX_RADIUS: Pixels = px(20.);
/// Send and Stop are round, this wide.
const ROUND_BUTTON: Pixels = px(30.);

impl Render for Composer {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let pill = theme.radius_full();
        let empty = self.draft.read(cx).value().trim().is_empty() && self.attachments.is_empty();
        let running = self.running.is_some();
        let foot = h_flex()
            .gap_1p5()
            .child(div().flex_1())
            .when_some(self.meter.clone(), |foot, meter| {
                foot.child(ContextRing { meter })
            })
            .when(running, |foot| {
                foot.child(
                    Button::new("composer:stop")
                        .icon(Glyph::Square)
                        .rounded(pill)
                        .with_size(ROUND_BUTTON)
                        .tooltip("Stop")
                        .on_click(cx.listener(|composer, _, _, cx| composer.stop(cx))),
                )
            })
            .child(
                Button::new("composer:send")
                    .primary()
                    .icon(Glyph::ArrowUp)
                    .rounded(pill)
                    .with_size(ROUND_BUTTON)
                    .disabled(empty)
                    .tooltip(if running { "Steer" } else { "Send" })
                    .on_click(cx.listener(|composer, _, window, cx| composer.send(window, cx))),
            );
        div().w_full().px_4().pt_1().pb_3().child(
            v_flex()
                .gap_2()
                .pl(px(14.))
                .pr(px(10.))
                .py(px(10.))
                .rounded(BOX_RADIUS)
                .bg(theme.group_box)
                .border_1()
                .border_color(theme.border)
                .child(Textarea::new(&self.draft).appearance(false))
                .child(foot),
        )
    }
}
