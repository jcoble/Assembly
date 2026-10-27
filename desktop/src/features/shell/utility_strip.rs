//! `UtilityStrip` owns the strip under the panes: Resources and Usage, each opening the kit's popover anchored to it
//! and opening upward, which the kit keeps on screen. Their bodies wait for backend feeds that don't exist yet
//! (resources and usage quota); nothing here polls. Replaces UtilityStrip.svelte, utilityStrip.ts and the usage and
//! resource popovers in ShellOverlays.svelte.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::popover::Popover;
use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::*;

pub struct UtilityStrip;

impl Render for UtilityStrip {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        h_flex()
            .h(px(28.))
            .flex_none()
            .px_2()
            .gap_2()
            .justify_end()
            .border_t_1()
            .border_color(cx.theme().border)
            .child(figure("resources", "Resources", "No resource figures yet."))
            .child(figure("usage", "Usage", "No usage figures yet."))
    }
}

/// A strip button whose popover opens above it. The body is only built while the popover is open.
fn figure(id: &'static str, label: &'static str, body: &'static str) -> impl IntoElement {
    div().debug_selector(move || format!("{id}-button")).child(
        Popover::new(id)
            .anchor(Anchor::BottomRight)
            .trigger(Button::new(label).ghost().compact().label(label))
            .content(move |_, _, _| {
                div()
                    .debug_selector(move || format!("{id}-body"))
                    .p_2()
                    .child(body)
            }),
    )
}
