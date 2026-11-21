//! `HomeButton` is the round Home button at the top left. Hovering it slides out a pill with Projects and Settings
//! (Messages has no view on main yet, so it is left out). A press always opens the providers and connections panel
//! (`HomePanel`) and never starts anything by itself. The top bar holds one `HomeButton`.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::popover::Popover;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::*;

use super::home_panel::HomePanel;
use crate::core::intents::open_settings::OpenSettings;
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::{home as ids, topbar};

/// The kit's circular button at the top bar's size.
const SIZE: Pixels = px(26.);
/// The slide-out's circles, a little smaller than Home.
const CIRCLE: Pixels = px(22.);

#[derive(Default)]
pub struct HomeButton {
    /// The pointer is over Home or its slide-out, so the slide-out is showing.
    hovered: bool,
    /// Exists only while the panel is open, so closing it drops the panel's subscription.
    panel: Option<Entity<HomePanel>>,
}

impl HomeButton {
    fn show_panel(&mut self, open: bool, cx: &mut Context<Self>) {
        match (open, self.panel.is_some()) {
            (true, false) => self.panel = Some(cx.new(HomePanel::new)),
            (false, true) => self.panel = None,
            _ => return,
        }
        cx.notify();
    }

    #[cfg(test)]
    pub(crate) fn panel(&self) -> Option<Entity<HomePanel>> {
        self.panel.clone()
    }
}

impl Render for HomeButton {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let home = Button::new("home")
            .primary()
            .icon(IconName::House)
            .with_size(SIZE)
            .rounded_full()
            .control(topbar::HOME, "Home")
            .tooltip("Providers and connections");
        // A weak capture keeps the kit's retained content builder from holding a closed panel alive.
        let panel = self.panel.as_ref().map(Entity::downgrade);
        let popover = Popover::new("home-panel")
            .anchor(Anchor::TopLeft)
            .p_0()
            .open(self.panel.is_some())
            .on_open_change(cx.listener(|button, open, _, cx| button.show_panel(*open, cx)))
            .trigger(home)
            .content(move |_, _, _| {
                div()
                    .overflow_hidden()
                    .children(panel.as_ref().and_then(WeakEntity::upgrade))
            });
        // The slide-out's circles sit on one pill beside Home.
        let slide = self.hovered.then(|| {
            h_flex()
                .ml(px(4.))
                .px(px(3.))
                .py(px(1.))
                .gap(px(2.))
                .rounded_full()
                .bg(cx.theme().secondary)
                .child(
                    circle("home-projects", IconName::Folder, ids::PROJECTS, "Projects").on_click(
                        |_, window, cx| crate::features::projects::projects_view::open(window, cx),
                    ),
                )
                .child(
                    circle(
                        "home-settings",
                        IconName::Settings,
                        ids::SETTINGS,
                        "Settings",
                    )
                    .on_click(|_, window, cx| window.dispatch_action(Box::new(OpenSettings), cx)),
                )
        });
        // Hovering anywhere over Home or the pill keeps the pill out.
        h_flex()
            .id("home-cluster")
            .h_full()
            .on_hover(cx.listener(|button, hovered: &bool, _, cx| {
                if button.hovered != *hovered {
                    button.hovered = *hovered;
                    cx.notify();
                }
            }))
            .child(div().debug_selector(|| "home-button".into()).child(popover))
            .children(slide)
    }
}

/// One round button on the slide-out.
fn circle(name: &'static str, icon: IconName, id: &'static str, label: &'static str) -> Button {
    Button::new(name)
        .ghost()
        .icon(icon)
        .with_size(CIRCLE)
        .rounded_full()
        .control(id, label)
        .tooltip(label)
}
