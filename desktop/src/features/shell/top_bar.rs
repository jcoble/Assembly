//! `TopBar` owns the title row above the panes, on the rail: the Home button, which keeps the open sessions column's width so the
//! dividers line up, also while the column's edge is dragged. Its menu opens Build Board and Settings,
//! and at the right the drawer toggle. Tool tabs and the Terminal action live in the drawer header's capsule/menu
//! (drawer_buttons.rs). Replaces TopTabRow.svelte's chrome, HomeButton.svelte and the
//! `window-chrome` markup in routes/+page.svelte.

use gpui_kit::assets::IconName;
use gpui_kit::component::Sizable as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{DockArea, DockPlacement};
use gpui_kit::component::h_flex;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::*;

use super::{drawer_buttons, top_tabs};
use crate::core::intents::open_settings::OpenSettings;

/// The row's height, the approved mock's `.titlerow`.
const HEIGHT: Pixels = px(40.);
/// The compact Home menu trigger uses the kit's circular button.
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
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let dock = self.dock.read(cx);
        let sessions = if dock.is_dock_open(DockPlacement::Left) {
            dock.dock_size(DockPlacement::Left).unwrap_or_default()
        } else {
            px(0.)
        };
        let home = Button::new("home")
            .primary()
            .icon(IconName::House)
            .with_size(HOME_SIZE)
            .rounded_full()
            .accessibility_label("Home")
            .tooltip("Home")
            .dropdown_menu(|menu, _, _| {
                menu.min_w(px(190.))
                    .item(
                        PopupMenuItem::new("Build Board")
                            .icon(IconName::LayoutDashboard)
                            .on_click(|_, window, cx| {
                                if let Ok(open) = cx.build_action("board::OpenBoard", None) {
                                    window.dispatch_action(open, cx);
                                }
                            }),
                    )
                    .separator()
                    .item(
                        PopupMenuItem::new("Settings")
                            .icon(IconName::Settings)
                            .on_click(|_, window, cx| {
                                window.dispatch_action(Box::new(OpenSettings), cx);
                            }),
                    )
            });
        h_flex()
            .h(HEIGHT)
            .flex_none()
            .pr(px(12.))
            .child(
                h_flex()
                    .w(sessions.max(MIN_HOME_WIDTH))
                    .h_full()
                    .gap_2()
                    .pl(px(80.))
                    .child(home),
            )
            .child(top_tabs::tabs(self.dock.clone(), window, cx))
            .child(drawer_buttons::toggle(self.dock.clone(), cx))
    }
}
