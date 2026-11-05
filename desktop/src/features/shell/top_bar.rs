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
use gpui_kit::component::popover::Popover;
use gpui_kit::*;

use super::{drawer_buttons, top_tabs};
use crate::activity::{DotChip, Tone};
use crate::core::engine::{EngineChanged, EngineFeed};
use crate::core::intents::open_settings::OpenSettings;
use crate::features::board::ToggleBoard;
use crate::features::board::board_panel::{BoardMemory, BoardPanel, top_bar_badge};
use crate::features::board::owner_notify::{OpenNeed, OwnerNotify};

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
    /// Exists only while the dropdown is open, so dismissal releases its pushed reads and clock.
    board: Option<Entity<BoardPanel>>,
    board_dismiss: Option<Subscription>,
    /// Where the panel was left, for the window's life.
    board_memory: BoardMemory,
    /// How many need the owner, read when the shared rows change.
    badge: Option<SharedString>,
    _feed: Subscription,
    /// A notification's click opens the panel on its question.
    _notifications: Option<Subscription>,
}

impl TopBar {
    pub fn new(dock: Entity<DockArea>, cx: &mut Context<Self>) -> Self {
        let _subscription = cx.observe(&dock, |_, _, cx| cx.notify());
        Self::register_board_action(cx.weak_entity(), cx);
        let feed = EngineFeed::global(cx);
        let badge = top_bar_badge(feed.read(cx));
        let _feed = cx.subscribe(&feed, |bar, feed, _: &EngineChanged, cx| {
            let badge = top_bar_badge(feed.read(cx));
            if bar.badge != badge {
                bar.badge = badge;
                cx.notify();
            }
        });
        let _notifications = OwnerNotify::try_global(cx).map(|notify| {
            cx.subscribe(&notify, |bar, _, OpenNeed(key): &OpenNeed, cx| {
                bar.show_board(true, cx);
                if let Some(board) = &bar.board {
                    board.update(cx, |board, cx| board.show_need(key, cx));
                }
            })
        });
        Self {
            dock,
            _subscription,
            _notifications,
            board: None,
            board_dismiss: None,
            board_memory: BoardMemory::default(),
            badge,
            _feed,
        }
    }
    fn register_board_action(bar: WeakEntity<Self>, cx: &mut App) {
        cx.on_action(move |_: &ToggleBoard, cx| {
            let bar = bar.clone();
            cx.defer(move |cx| {
                _ = bar.update(cx, |bar, cx| bar.show_board(bar.board.is_none(), cx));
            });
        });
    }

    fn show_board(&mut self, open: bool, cx: &mut Context<Self>) {
        if open && self.board.is_none() {
            let memory = self.board_memory.clone();
            let board = cx.new(|cx| BoardPanel::new(memory, cx));
            self.board_dismiss = Some(cx.subscribe(&board, |bar, _, _: &DismissEvent, cx| {
                bar.show_board(false, cx)
            }));
            self.board = Some(board);
        } else if !open {
            self.board_dismiss = None;
            if let Some(board) = self.board.take() {
                self.board_memory = board.read(cx).memory();
            }
        }
        cx.notify();
    }

    #[cfg(test)]
    pub(crate) fn badge_words(&self) -> Option<SharedString> {
        self.badge.clone()
    }

    #[cfg(test)]
    pub(crate) fn board_reader(&self) -> Option<WeakEntity<BoardPanel>> {
        self.board.as_ref().map(Entity::downgrade)
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
                                if let Ok(open) = cx.build_action("board::ToggleBoard", None) {
                                    window.dispatch_action(open, cx);
                                }
                            }),
                    )
                    .item(PopupMenuItem::new("Projects").on_click(|_, window, cx| {
                        crate::features::projects::projects_view::open(window, cx)
                    }))
                    .separator()
                    .item(
                        PopupMenuItem::new("Settings")
                            .icon(IconName::Settings)
                            .on_click(|_, window, cx| {
                                super::workspace_preview::open(true, window, cx)
                            }),
                    )
                    .item(
                        PopupMenuItem::new("Connection settings")
                            .icon(IconName::Settings)
                            .on_click(|_, window, cx| {
                                window.dispatch_action(Box::new(OpenSettings), cx);
                            }),
                    )
            });
        let trigger = Button::new("board").ghost().small().label("Board");
        let trigger = match self.badge.clone() {
            Some(count) => trigger.child(
                div()
                    .debug_selector(|| "board-badge".into())
                    .child(DotChip::new(count, Tone::Warning)),
            ),
            None => trigger,
        };
        let trigger = trigger.tooltip("Build Board");
        // A weak capture prevents the kit's retained content builder from keeping a dismissed Board alive.
        let board = self.board.as_ref().map(Entity::downgrade);
        let dropdown = Popover::new("build-board")
            .anchor(Anchor::TopRight)
            .p_0()
            .open(self.board.is_some())
            .on_open_change(cx.listener(|bar, open, _, cx| bar.show_board(*open, cx)))
            .trigger(trigger)
            .content(move |_, _, _| {
                div()
                    .debug_selector(|| "board-dropdown-body".into())
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
            .child(top_tabs::tabs(self.dock.clone(), window, cx))
            .child(drawer_buttons::toggle(self.dock.clone(), cx))
    }
}
