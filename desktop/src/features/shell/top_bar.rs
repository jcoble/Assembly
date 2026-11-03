//! `TopBar` owns the title row above the panes, on the rail: the Home button, which keeps the open sessions column's width so the
//! dividers line up, also while the column's edge is dragged, the Board button beside it, which opens the Build Board,
//! and at the right the drawer's pane buttons and Terminal
//! (drawer_buttons.rs). Replaces TopTabRow.svelte's chrome, HomeButton.svelte and the
//! `window-chrome` markup in routes/+page.svelte.

use gpui_kit::assets::IconName;
use gpui_kit::component::Sizable as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{DockArea, DockPlacement};
use gpui_kit::component::popover::Popover;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::*;

use super::drawer_buttons::DrawerButtons;
use crate::features::board::OpenBoard;
use crate::features::board::board_pane::BoardPane;

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
    /// Exists only while the dropdown is open, so dismissal releases its pushed reads and clock.
    board: Option<Entity<BoardPane>>,
    board_dismiss: Option<Subscription>,
}

impl TopBar {
    pub fn new(dock: Entity<DockArea>, cx: &mut Context<Self>) -> Self {
        let _subscription = cx.observe(&dock, |_, _, cx| cx.notify());
        Self::register_board_action(cx.weak_entity(), cx);
        Self {
            dock,
            _subscription,
            board: None,
            board_dismiss: None,
        }
    }

    fn register_board_action(bar: WeakEntity<Self>, cx: &mut App) {
        cx.on_action(move |_: &OpenBoard, cx| {
            let bar = bar.clone();
            cx.defer(move |cx| {
                _ = bar.update(cx, |bar, cx| bar.show_board(true, cx));
            });
        });
    }

    fn show_board(&mut self, open: bool, cx: &mut Context<Self>) {
        if open && self.board.is_none() {
            let board = cx.new(BoardPane::new);
            self.board_dismiss = Some(cx.subscribe(&board, |bar, _, _: &DismissEvent, cx| {
                bar.show_board(false, cx)
            }));
            self.board = Some(board);
        } else if !open {
            self.board_dismiss = None;
            self.board = None;
        }
        cx.notify();
    }

    #[cfg(test)]
    pub(crate) fn board_reader(&self) -> Option<WeakEntity<BoardPane>> {
        self.board.as_ref().map(Entity::downgrade)
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
        // A weak capture prevents the kit's retained content builder from keeping a dismissed Board alive.
        let board = self.board.as_ref().map(Entity::downgrade);
        let dropdown = Popover::new("build-board")
            .anchor(Anchor::TopRight)
            .p_0()
            .open(self.board.is_some())
            .on_open_change(cx.listener(|bar, open, _, cx| bar.show_board(*open, cx)))
            .trigger(
                Button::new("board")
                    .ghost()
                    .small()
                    .label("Board")
                    .tooltip("Build Board"),
            )
            .content(move |_, window, _| {
                // Old Board.svelte caps width at 900px and keeps 8px side / 36px bottom clearance.
                // The 40px title row, 8px inset and 4px popover gap leave the rest for the virtual list.
                let viewport = window.viewport_size();
                div()
                    .debug_selector(|| "board-dropdown-body".into())
                    .w((viewport.width - px(16.)).min(px(900.)).max(px(0.)))
                    .h((viewport.height - px(88.)).max(px(0.)))
                    .overflow_hidden()
                    .children(board.as_ref().and_then(WeakEntity::upgrade))
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
                    .child(home)
                    .child(
                        div()
                            .debug_selector(|| "board-dropdown-button".into())
                            .child(dropdown),
                    ),
            )
            .child(div().flex_1())
            .child(DrawerButtons {
                dock: self.dock.clone(),
            })
    }
}
