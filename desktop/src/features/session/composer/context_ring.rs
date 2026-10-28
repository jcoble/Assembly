//! `ContextRing` is the composer's gauge of the agent's context window: a ring of what's left, muted until a quarter is
//! left, warm then and hot at a tenth, with "51,285 of 1,000,000 (5%)" on hover. It reads as a gauge, never a spinner
//! (the old app's TSK-1367): the kit's `ProgressCircle` holding still at its value. Replaces the meter in
//! Composer.svelte.

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::component::progress::ProgressCircle;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::*;

use super::context_meter::ContextMeter;

#[derive(IntoElement)]
pub struct ContextRing {
    pub meter: ContextMeter,
}

impl RenderOnce for ContextRing {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let color = match (self.meter.hot, self.meter.warm) {
            (true, _) => theme.danger,
            (_, true) => theme.warning,
            _ => theme.muted_foreground,
        };
        let detail = SharedString::from(self.meter.detail);
        div()
            .id("composer:context")
            .size(px(22.))
            .flex()
            .items_center()
            .justify_center()
            .tooltip(move |window, cx| Tooltip::new(detail.clone()).build(window, cx))
            .child(
                ProgressCircle::new("composer:context-ring")
                    .value(f32::from(self.meter.arc))
                    .color(color),
            )
    }
}
