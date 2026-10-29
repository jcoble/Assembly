//! `TopBar` owns the title row above the panes, on the rail: the Home button, which keeps the open sessions column's width so the
//! dividers line up, also while the column's edge is dragged, the Board button beside it, which opens the Build Board,
//! and at the right the drawer's pane buttons and Terminal
//! (drawer_buttons.rs). Replaces TopTabRow.svelte's chrome, HomeButton.svelte and the
//! `window-chrome` markup in routes/+page.svelte.

use gpui_kit::assets::IconName;
use gpui_kit::component::Sizable as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{DockArea, DockPlacement};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::*;

use super::drawer_buttons::DrawerButtons;

/// The row's height, the approved mock's `.titlerow`.
const HEIGHT: Pixels = px(40.);
/// The Home button's size, the mock's 26 px icon button (`.ib`) with 8 px corners and a 15 px icon.
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
        let theme = cx.theme();
        let home = div()
            .id("home")
            .size(HOME_SIZE)
            .flex()
            .items_center()
            .justify_center()
            .rounded(px(8.))
            .text_color(theme.muted_foreground)
            .hover(|button| button.bg(theme.secondary_hover))
            .tooltip(|window, cx| Tooltip::new("Home").build(window, cx))
            .child(Icon::new(IconName::House).size(px(15.)));
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
                    .child(home)
                    .child(
                        Button::new("board")
                            .ghost()
                            .small()
                            .label("Board")
                            .tooltip("Build Board")
                            .on_click(|_, window, cx| {
                                // By name, so the shell doesn't reach into the board feature's folder.
                                if let Ok(open) = cx.build_action("board::OpenBoard", None) {
                                    window.dispatch_action(open, cx);
                                }
                            }),
                    ),
            )
            .child(div().flex_1())
            .child(DrawerButtons {
                dock: self.dock.clone(),
            })
    }
}
