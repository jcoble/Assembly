//! `UtilityStrip` owns the bar at the foot of the sessions column, the look's 46-high footer: Settings, Resources
//! and Usage as quiet buttons, each opening the kit's popover anchored to it and opening upward, which the kit keeps on screen.
//! Settings opens the Settings page through the `OpenSettings` intent; the other two bodies wait for the resources and
//! usage quota feeds; nothing here polls. Replaces UtilityStrip.svelte, utilityStrip.ts and the usage and resource popovers in ShellOverlays.svelte.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::Button;
use gpui_kit::component::h_flex;
use gpui_kit::component::popover::Popover;
use gpui_kit::*;

use crate::components::quiet_button::quiet_button;
use crate::core::intents::open_settings::OpenSettings;
use crate::core::theme::metrics::{CRUMB_HEIGHT, SPACE};
use crate::core::ui::access::control_ids::utility as ids;
use crate::core::ui::access::{Control as _, say};

pub struct UtilityStrip;

impl Render for UtilityStrip {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        // The mock's `.foot`: three quiet buttons, each an icon and its word in text-2.
        let button = |id: &'static str, icon: IconName| quiet_button(id, icon, id, cx);
        let settings = button("Settings", IconName::Settings)
            .control(ids::SETTINGS, "Settings")
            .tooltip("Settings")
            .on_click(|_, window, cx| window.dispatch_action(Box::new(OpenSettings), cx));
        h_flex()
            .h(CRUMB_HEIGHT)
            .flex_none()
            .px(SPACE[2])
            .gap(SPACE[0])
            .child(
                div()
                    .debug_selector(|| "settings-button".into())
                    .child(settings),
            )
            .child(figure(
                "resources",
                button("Resources", IconName::Activity).control(ids::RESOURCES, "Resources"),
                (ids::RESOURCES_EMPTY, "No resource figures yet."),
            ))
            .child(figure(
                "usage",
                button("Usage", IconName::ChartNoAxesColumn).control(ids::USAGE, "Usage"),
                (ids::USAGE_EMPTY, "No usage figures yet."),
            ))
    }
}

/// A strip button whose popover opens above it. The body is only built while the popover is open.
fn figure(
    id: &'static str,
    trigger: Button,
    (body_id, body): (&'static str, &'static str),
) -> impl IntoElement {
    div().debug_selector(move || format!("{id}-button")).child(
        Popover::new(id)
            .anchor(Anchor::BottomLeft)
            .trigger(trigger)
            .content(move |_, _, _| {
                say(body_id, body)
                    .debug_selector(move || format!("{id}-body"))
                    .p_2()
            }),
    )
}
