//! `UtilityStrip` owns the bar at the foot of the sessions column, under a divider: Settings, then Resources and
//! Usage as pills, each opening the kit's popover anchored to it and opening upward, which the kit keeps on screen.
//! Settings opens the Settings page through the `OpenSettings` intent; the other two bodies wait for the resources and
//! usage quota feeds; nothing here polls. Replaces UtilityStrip.svelte, utilityStrip.ts and the usage and resource popovers in ShellOverlays.svelte.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::popover::Popover;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::*;

use crate::core::intents::open_settings::OpenSettings;

pub struct UtilityStrip;

impl Render for UtilityStrip {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let pill = theme.radius_full();
        // The mock's foot: a 22 px settings button with 6 px corners, then its figures.
        let settings = Button::new("Settings")
            .ghost()
            .with_size(px(17.))
            .size(px(22.))
            .rounded(px(6.))
            .icon(IconName::Settings)
            .tooltip("Settings")
            .on_click(|_, window, cx| window.dispatch_action(Box::new(OpenSettings), cx));
        h_flex()
            .h(px(40.))
            .flex_none()
            .px(px(12.))
            .gap(px(10.))
            .border_t_1()
            .border_color(theme.table_row_border)
            .text_color(theme.muted_foreground)
            .child(
                div()
                    .debug_selector(|| "settings-button".into())
                    .child(settings),
            )
            .child(figure(
                "resources",
                Button::new("Resources")
                    .ghost()
                    .compact()
                    .rounded(pill)
                    .label("Resources"),
                "No resource figures yet.",
            ))
            .child(figure(
                "usage",
                Button::new("Usage")
                    .ghost()
                    .compact()
                    .rounded(pill)
                    .label("Usage"),
                "No usage figures yet.",
            ))
    }
}

/// A strip button whose popover opens above it. The body is only built while the popover is open.
fn figure(id: &'static str, trigger: Button, body: &'static str) -> impl IntoElement {
    div().debug_selector(move || format!("{id}-button")).child(
        Popover::new(id)
            .anchor(Anchor::BottomLeft)
            .trigger(trigger)
            .content(move |_, _, _| {
                div()
                    .debug_selector(move || format!("{id}-body"))
                    .p_2()
                    .child(body)
            }),
    )
}
