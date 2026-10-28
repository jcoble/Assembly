//! `TopBar` owns the title row above the panes, on the rail: the round Home button, whose menu opens Settings, and
//! which keeps the open sessions column's width so the dividers line up, also while the column's edge is dragged. The
//! dock's own tab bars are the tabs, and the kit draws the sessions, drawer and bottom dock toggles in them. Replaces
//! TopTabRow.svelte's chrome, HomeButton.svelte and the `window-chrome` markup in routes/+page.svelte.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{DockArea, DockPlacement};
use gpui_kit::component::h_flex;
use gpui_kit::component::menu::DropdownMenu as _;
use gpui_kit::component::{ActiveTheme as _, Sizable as _};
use gpui_kit::*;

use crate::core::intents::open_settings::OpenSettings;

/// The row's height, the old app's `--center-head-row-height` (lib/shell/styles/nextTokens.css:287).
const HEIGHT: Pixels = px(42.);
/// The round Home button's size (the design system's: the send button is 30 px, Home 26). Its icon is one the kit
/// embeds by default; a house would need an asset source of our own.
const HOME_SIZE: Pixels = px(26.);
/// The Home cap's narrowest width, when the sessions column is folded or narrow (routes/+page.svelte:1045-1052).
const MIN_HOME_WIDTH: Pixels = px(172.);

pub struct TopBar {
    dock: Entity<DockArea>,
    /// Redraws the bar whenever the dock does, so the Home cap follows a drag.
    _subscription: Subscription,
}

impl TopBar {
    pub fn new(dock: Entity<DockArea>, cx: &mut Context<Self>) -> Self {
        let _subscription = cx.observe(&dock, |_, _, cx| cx.notify());
        Self {
            dock,
            _subscription,
        }
    }
}

impl Render for TopBar {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let dock = self.dock.read(cx);
        let sessions = if dock.is_dock_open(DockPlacement::Left) {
            dock.dock_size(DockPlacement::Left).unwrap_or_default()
        } else {
            px(0.)
        };
        h_flex().h(HEIGHT).flex_none().child(
            h_flex()
                .w(sessions.max(MIN_HOME_WIDTH))
                .h_full()
                .pl(px(80.))
                .child(
                    Button::new("home")
                        .primary()
                        .icon(IconName::House)
                        .rounded(cx.theme().radius_full())
                        .with_size(HOME_SIZE)
                        .dropdown_menu(|menu, _, _| {
                            menu.menu_with_icon(
                                "Settings",
                                IconName::Settings,
                                Box::new(OpenSettings),
                            )
                        }),
                ),
        )
    }
}
