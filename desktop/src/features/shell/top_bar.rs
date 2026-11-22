//! `TopBar` owns the window's top row, on the glass, as the rule sheet's "Window chrome and top bar" draws it. On the
//! left: the window buttons (on macOS the title bar is transparent and its buttons sit inside this row), Home as an
//! icon button whose menu opens Build Board and Settings, and Board as an outlined pill with its grey count of live
//! sessions. The left part keeps the sessions column's width, so the tabs start over the chat. In the middle: the
//! open tabs (top_tabs.rs). On the right: the amber "N need you" pill when something needs the owner, which opens
//! the Board on what needs you, then the Terminal and drawer toggles. Empty glass in the row moves the window and a
//! double click zooms it, as a title bar does. Replaces TopTabRow.svelte's chrome, HomeButton.svelte and the
//! `window-chrome` markup in routes/+page.svelte.

use crate::components::glass_button::{GLASS_ICON, glass_button, glass_icon_button};
use crate::features::remote_install::restart_button::RestartButton;
use gpui_kit::assets::IconName;
use gpui_kit::component::dock::{DockArea, DockPlacement};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::popover::Popover;
use gpui_kit::component::{Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::{drawer_buttons, terminal_toggle, top_tabs};
use crate::core::engine::{EngineChanged, EngineFeed};
use crate::core::intents::open_settings::OpenSettings;
use crate::core::theme::look::Look;
use crate::core::theme::metrics::{
    CORNER_PILL, COUNT_HEIGHT, COUNT_MIN_WIDTH, SPACE, TOP_BAR_HEIGHT,
};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::topbar as ids;
use crate::features::board::ToggleBoard;
use crate::features::board::board_panel::{BoardMemory, BoardPanel, TopBarCounts};
use crate::features::board::board_prefs::BoardTab;
use crate::features::board::owner_notify::{OpenNeed, OwnerNotify};

/// From the window's left edge to the row's first control: the mockup's 18 before the window buttons.
const LEFT_PADDING: Pixels = px(18.);
/// The macOS window buttons (three 12px lights 8 apart) and the 16 after them, the mockup's `.lights`.
const LIGHTS_WIDTH: Pixels = px(68.);
/// The height AppKit gives a window button; the buttons are centred in the row from it.
const LIGHT_HEIGHT: Pixels = px(14.);
/// Board's padding after its count, the mockup's `.navb`.
const BOARD_PADDING_RIGHT: Pixels = px(5.);
/// The right part's padding, the mockup's `.bar-r`.
const RIGHT_PADDING_LEFT: Pixels = SPACE[7];
const RIGHT_PADDING_RIGHT: Pixels = SPACE[6];

/// The window's options: on macOS a transparent title bar whose buttons sit inside the top bar, with the app moving
/// the window itself (see `drag_area`). Elsewhere the system draws its own title bar above the row, as before.
pub fn window_options() -> WindowOptions {
    let macos = cfg!(target_os = "macos");
    WindowOptions {
        titlebar: Some(TitlebarOptions {
            title: Some("Assembly".into()),
            appears_transparent: macos,
            traffic_light_position: macos
                .then(|| point(LEFT_PADDING, (TOP_BAR_HEIGHT - LIGHT_HEIGHT) / 2.)),
        }),
        app_owns_titlebar_drag: macos,
        ..Default::default()
    }
}

pub struct TopBar {
    dock: Entity<DockArea>,
    /// Redraws the bar whenever the dock does, so the Home cap follows a drag.
    _subscription: Subscription,
    /// Exists only while the dropdown is open, so dismissal releases its pushed reads and clock.
    board: Option<Entity<BoardPanel>>,
    board_dismiss: Option<Subscription>,
    /// Where the panel was left, for the window's life.
    board_memory: BoardMemory,
    /// What needs the owner and how many sessions are live, read when the shared rows change.
    counts: TopBarCounts,
    /// Set by a press on empty glass, so the next pointer move starts moving the window.
    should_move: bool,
    restart: Entity<RestartButton>,
    _feed: Subscription,
    /// A notification's click opens the panel on its question.
    _notifications: Option<Subscription>,
}

impl TopBar {
    pub fn new(dock: Entity<DockArea>, cx: &mut Context<Self>) -> Self {
        let _subscription = cx.observe(&dock, |_, _, cx| cx.notify());
        Self::register_board_action(cx.weak_entity(), cx);
        let feed = EngineFeed::global(cx);
        let counts = TopBarCounts::read(feed.read(cx));
        let _feed = cx.subscribe(&feed, |bar, feed, _: &EngineChanged, cx| {
            let counts = TopBarCounts::read(feed.read(cx));
            if bar.counts != counts {
                bar.counts = counts;
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
            restart: cx.new(RestartButton::new),
            dock,
            _subscription,
            _notifications,
            board: None,
            board_dismiss: None,
            board_memory: BoardMemory::default(),
            counts,
            should_move: false,
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
        self.counts.needs.clone()
    }

    #[cfg(test)]
    pub(crate) fn board_reader(&self) -> Option<WeakEntity<BoardPanel>> {
        self.board.as_ref().map(Entity::downgrade)
    }
}

impl TopBar {
    /// Opens the Board on what needs the owner: the "N need you" pill's press.
    fn show_needs(&mut self, cx: &mut Context<Self>) {
        self.show_board(true, cx);
        if let Some(board) = &self.board {
            board.update(cx, |board, cx| board.show_tab(BoardTab::Needs, cx));
        }
    }

    /// Empty glass that moves the window, as a title bar does: a press then a move starts the system's window move,
    /// and a double click zooms it as the system's setting says. Only on macOS, where the app owns the title bar;
    /// elsewhere the system's own title bar does this.
    fn drag_area(&self, id: &'static str, cx: &mut Context<Self>) -> Stateful<Div> {
        div()
            .id(id)
            .h_full()
            .when(cfg!(target_os = "macos"), |area| {
                area.window_control_area(WindowControlArea::Drag)
                    .on_mouse_down(
                        MouseButton::Left,
                        cx.listener(|bar, _, _, _| bar.should_move = true),
                    )
                    .on_mouse_up(
                        MouseButton::Left,
                        cx.listener(|bar, _, _, _| bar.should_move = false),
                    )
                    .on_mouse_down_out(cx.listener(|bar, _, _, _| bar.should_move = false))
                    .on_mouse_move(cx.listener(|bar, _, window, _| {
                        if bar.should_move {
                            bar.should_move = false;
                            window.start_window_move();
                        }
                    }))
                    .on_click(|event, window, _| {
                        if event.click_count() == 2 {
                            window.titlebar_double_click();
                        }
                    })
            })
    }

    /// Board: an outlined pill with its grey count of live sessions, opening the Board under it.
    fn board_button(&self, look: &Look, cx: &mut Context<Self>) -> impl IntoElement {
        let count = div()
            .flex()
            .items_center()
            .justify_center()
            .min_w(COUNT_MIN_WIDTH)
            .h(COUNT_HEIGHT)
            .px(SPACE[2])
            .rounded(CORNER_PILL)
            .bg(look.on_glass_count)
            .text_color(look.on_glass_text_2)
            .text_role(TextRole::Meta, cx)
            .font_weight(FontWeight::SEMIBOLD)
            .child(self.counts.live.to_string());
        // The kit's button puts its children in a row of its own, so the words carry their text role themselves.
        let trigger = glass_button("board", self.board.is_some(), cx)
            .control(ids::BOARD, "Board")
            .border_1()
            .border_color(look.on_glass_line_chip)
            .pl(SPACE[5])
            .pr(BOARD_PADDING_RIGHT)
            .tooltip("Build Board")
            .child(Icon::new(IconName::LayoutDashboard).size(GLASS_ICON))
            .child(
                div()
                    .text_role(TextRole::Body, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .child("Board"),
            )
            .child(count);
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
        div()
            .debug_selector(|| "board-dropdown-button".into())
            .child(dropdown)
    }

    /// "N need you": amber outline on the inset glass fill, only while something needs the owner.
    fn needs_pill(&self, look: &Look, cx: &mut Context<Self>) -> Option<impl IntoElement> {
        let count = self.counts.needs.clone()?;
        let pill = glass_button("needs-you", false, cx)
            .control(ids::BOARD_BADGE, format!("{count} need you"))
            .pl(SPACE[4])
            .pr(SPACE[6])
            .bg(look.on_glass_inset)
            .border_1()
            .border_color(look.needs_you_line)
            .tooltip("Open the Board on what needs you")
            .child(
                Icon::new(IconName::CircleQuestionMark)
                    .size(GLASS_ICON)
                    .text_color(look.needs_you),
            )
            .child(
                h_flex()
                    .gap(SPACE[1])
                    .text_color(look.on_glass_text_1)
                    .text_role(TextRole::Body, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .child(div().font_weight(FontWeight(650.)).child(count))
                    .child("need you"),
            )
            .on_click(cx.listener(|bar, _, _, cx| bar.show_needs(cx)));
        Some(div().debug_selector(|| "board-badge".into()).child(pill))
    }

    /// Home: a glass icon button whose menu opens Build Board, Projects and the settings.
    fn home_button(cx: &App) -> impl IntoElement {
        glass_icon_button("home", IconName::House, false, cx)
            .control(ids::HOME, "Home")
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
            })
    }
}

impl Render for TopBar {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let look = Look::of(cx);
        // The left part spans the sessions column and the right part the drawer, so their edges line up with the
        // tiles under them, while each is at least as wide as what it holds.
        let dock = self.dock.read(cx);
        let width_of = |placement| {
            if dock.is_dock_open(placement) {
                dock.dock_size(placement).unwrap_or_default()
            } else {
                px(0.)
            }
        };
        let (sessions, drawer) = (
            width_of(DockPlacement::Left),
            width_of(DockPlacement::Right),
        );
        // The window buttons sit in the row on macOS, except in full screen, where the system hides them.
        let lights = cfg!(target_os = "macos") && !window.is_fullscreen();
        let left = h_flex()
            .flex_none()
            .min_w(sessions)
            .h_full()
            .gap(SPACE[2])
            .child(
                self.drag_area("top-bar-lights", cx)
                    .flex_none()
                    .w(LEFT_PADDING + if lights { LIGHTS_WIDTH } else { px(0.) }),
            )
            .child(Self::home_button(cx))
            .child(self.board_button(&look, cx))
            .child(self.drag_area("top-bar-left-glass", cx).flex_1());
        let right = h_flex()
            .flex_none()
            .min_w(drawer)
            .h_full()
            .gap(SPACE[1])
            .pl(RIGHT_PADDING_LEFT)
            .pr(RIGHT_PADDING_RIGHT)
            .children(self.needs_pill(&look, cx))
            .child(self.drag_area("top-bar-right-glass", cx).flex_1())
            .child(self.restart.clone())
            .child(terminal_toggle::button(self.dock.clone(), cx))
            .child(drawer_buttons::toggle(self.dock.clone(), cx));
        h_flex()
            .h(TOP_BAR_HEIGHT)
            .flex_none()
            .child(left)
            .child(top_tabs::tabs(self.dock.clone(), window, cx))
            .child(self.drag_area("top-bar-glass", cx).flex_1().min_w(SPACE[7]))
            .child(right)
    }
}
