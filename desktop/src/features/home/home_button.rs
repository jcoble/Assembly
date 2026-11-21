//! `HomeButton` is the round Home button at the top left. Hovering it slides out a pill with Projects and Settings
//! (Messages has no view on main yet, so it is left out). A press always opens the providers and connections panel
//! (`HomePanel`) and never starts anything by itself. While the panel is closed, the button observes the tiny
//! `HomeSummary` feed (`settings_summary`) to show a static progress ring while Assembly works on its own and turn
//! amber only when the user is needed. The top bar holds one `HomeButton`.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonCustomVariant, ButtonVariants as _};
use gpui_kit::component::popover::Popover;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::*;

use super::home_panel::HomePanel;
use super::home_summary::{GlobalHomeSummary, HomeSummary};
use super::settings_state::Summary;
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
    /// The latest backend-derived summary for the ring and amber state while the panel is closed.
    summary: Summary,
    /// Subscription to the active `HomeSummary` entity once started.
    _summary: Option<Subscription>,
    /// Observes the global slot so the button attaches even when created before the socket starts.
    _follow: Option<Subscription>,
}

impl HomeButton {
    /// Creates the top bar's Home button and attaches it to `HomeSummary` whenever the socket starts.
    pub fn new(cx: &mut Context<Self>) -> Self {
        let mut button = Self {
            _follow: Some(cx.observe_global::<GlobalHomeSummary>(Self::follow)),
            ..Self::default()
        };
        button.follow(cx);
        button
    }

    /// Attaches to the global `HomeSummary` once `main.rs` has started it on the backend's ready line.
    fn follow(&mut self, cx: &mut Context<Self>) {
        let feed = HomeSummary::try_global(cx);
        self._summary = feed
            .as_ref()
            .map(|feed| cx.observe(feed, |button, feed, cx| button.read_summary(&feed, cx)));
        if let Some(feed) = feed {
            self.read_summary(&feed, cx);
        }
    }

    /// Copies the latest backend summary into the button and redraws only when it changed.
    fn read_summary(&mut self, feed: &Entity<HomeSummary>, cx: &mut Context<Self>) {
        let next = feed.read(cx).summary().cloned().unwrap_or_default();
        if self.summary != next {
            self.summary = next;
            cx.notify();
        }
    }

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
        let theme = cx.theme();
        let tooltip = if (self.summary.needs_user || self.summary.working)
            && !self.summary.message.is_empty()
        {
            self.summary.message.clone()
        } else {
            "Providers and connections".to_string()
        };
        // Turn amber only when the backend says the user is needed; stay primary otherwise.
        let home = if self.summary.needs_user {
            let amber = ButtonCustomVariant::new(cx)
                .color(theme.warning)
                .foreground(theme.warning_foreground)
                .hover(theme.warning.opacity(0.9))
                .active(theme.warning.opacity(0.8));
            Button::new("home").custom(amber)
        } else {
            Button::new("home").primary()
        }
        .icon(IconName::House)
        .with_size(SIZE)
        .rounded_full()
        .control(topbar::HOME, "Home")
        .tooltip(tooltip);
        // A static circular ring shows while Assembly works on its own: zero repeating animations, zero idle CPU.
        let ring = if self.summary.working {
            theme.info
        } else {
            gpui::transparent_black()
        };
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
                .bg(theme.secondary)
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
            .child(
                div()
                    .debug_selector(|| "home-button".into())
                    .rounded_full()
                    .p(px(2.))
                    .border_2()
                    .border_color(ring)
                    .child(popover),
            )
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
