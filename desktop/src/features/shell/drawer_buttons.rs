//! The drawer's compact tool capsule, overflow menu and close button. The dock remains the authority for the shown
//! pane and its saved layout; selecting an overflow tool adds just its icon to the primary capsule.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{DockArea, DockPlacement, PaneRef, PanelId};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::default_layout::{
    AGENTS, CONTEXT, FILES, HISTORY, RUN, SOURCE_CONTROL, TASKS, TERMINAL, WORKTREES,
};

const SIZE: Pixels = px(30.);

/// What pressing a drawer pane's button does.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Press {
    /// The drawer is shut: open it on this pane.
    Open,
    /// The drawer shows another pane: show this one.
    Show,
    /// The drawer shows this pane: close it.
    Close,
}

/// What a press does, from whether the drawer is open and the pane is the one it shows.
pub fn press(open: bool, shown: bool) -> Press {
    match (open, shown) {
        (false, _) => Press::Open,
        (true, true) => Press::Close,
        (true, false) => Press::Show,
    }
}

#[derive(IntoElement)]
pub struct DrawerButtons {
    pub dock: Entity<DockArea>,
}

impl RenderOnce for DrawerButtons {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let area = self.dock.read(cx);
        let (open, terminal_open) = (
            area.is_dock_open(DockPlacement::Right),
            area.is_dock_open(DockPlacement::Bottom),
        );
        // Each visible drawer pane: its id, its name, and whether its tab group shows it.
        let mut panes: Vec<(PanelId, &'static str, bool)> = Vec::new();
        if let Some(tree) = area.layout(DockPlacement::Right) {
            tree.root().walk(&mut |node| {
                if let PaneRef::Tabs { panels, active_ix } = node.kind() {
                    for (ix, id) in panels.iter().enumerate() {
                        if let Some(panel) = area.panel(*id).filter(|panel| panel.visible(cx)) {
                            panes.push((*id, panel.panel_name(cx), ix == active_ix));
                        }
                    }
                }
            });
        }
        let theme = cx.theme();
        let (lit, color, foreground, capsule) = (
            theme.secondary_hover,
            theme.muted_foreground,
            theme.foreground,
            theme.secondary,
        );
        let button = |name: &'static str, glyph: IconName, on: bool| {
            div()
                .id(name)
                .debug_selector(move || format!("drawer-button-{name}"))
                .size(SIZE)
                .flex()
                .items_center()
                .justify_center()
                .rounded_full()
                .text_color(color)
                .cursor_pointer()
                .when(on, |button| button.bg(lit).text_color(foreground))
                .hover(|button| button.bg(lit))
                .tooltip(move |window, cx| Tooltip::new(name).build(window, cx))
                .child(Icon::new(glyph).size(px(17.)))
        };
        let overflow: Vec<_> = panes
            .iter()
            .copied()
            .filter(|(_, name, _)| !primary(name))
            .collect();
        let panes = panes
            .into_iter()
            .filter(|(_, name, shown)| primary(name) || *shown)
            .map(|(id, name, shown)| {
                let dock = self.dock.clone();
                button(name, icon(name), open && shown).on_click(move |_, window, cx| {
                    cx.defer(crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed);
                    dock.update(cx, |dock, cx| match press(open, shown) {
                        Press::Open => {
                            dock.select_panel(id, window, cx);
                            dock.toggle_dock(DockPlacement::Right, window, cx);
                        }
                        Press::Show => dock.select_panel(id, window, cx),
                        Press::Close => dock.toggle_dock(DockPlacement::Right, window, cx),
                    })
                })
            });
        let dock = self.dock.clone();
        let menu = Button::new("drawer-overflow")
            .ghost()
            .icon(Icon::new(IconName::Ellipsis).size(px(18.)))
            .w(SIZE)
            .h(SIZE)
            .rounded(px(15.))
            .tooltip("More tools")
            .dropdown_menu_with_anchor(Anchor::TopRight, move |menu, _, _| {
                let menu =
                    overflow
                        .iter()
                        .fold(menu.min_w(px(190.)), |menu, &(id, name, shown)| {
                            let dock = dock.clone();
                            menu.item(
                                PopupMenuItem::new(name)
                                    .icon(icon(name))
                                    .checked(open && shown)
                                    .on_click(move |_, window, cx| {
                    cx.defer(crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed);
                                        dock.update(cx, |dock, cx| {
                                            dock.select_panel(id, window, cx)
                                        });
                                    }),
                            )
                        });
                let dock = dock.clone();
                menu.separator().item(
                    PopupMenuItem::new(TERMINAL)
                        .icon(IconName::SquareTerminal)
                        .checked(terminal_open)
                        .on_click(move |_, window, cx| {
                    cx.defer(crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed);
                            dock.update(cx, |dock, cx| {
                                dock.toggle_dock(DockPlacement::Bottom, window, cx)
                            });
                        }),
                )
            });
        let dock = self.dock.clone();
        let close = button("Close tools", IconName::X, false).on_click(move |_, window, cx| {
                    cx.defer(crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed);
            dock.update(cx, |dock, cx| {
                dock.toggle_dock(DockPlacement::Right, window, cx)
            })
        });
        h_flex()
            .h(px(48.))
            .px_2()
            .gap_1()
            .child(
                h_flex()
                    .flex_none()
                    .p(px(3.))
                    .gap(px(2.))
                    .rounded_full()
                    .bg(capsule)
                    .children(panes),
            )
            .child(div().flex_1())
            .child(menu)
            .child(close)
    }
}

fn primary(name: &str) -> bool {
    matches!(
        name,
        SOURCE_CONTROL | AGENTS | TASKS | WORKTREES | RUN | CONTEXT
    )
}

/// The chrome keeps a single, discoverable control while the drawer is closed.
pub fn toggle(dock: Entity<DockArea>, cx: &App) -> impl IntoElement {
    let open = dock.read(cx).is_dock_open(DockPlacement::Right);
    let theme = cx.theme();
    div()
        .id("drawer-toggle")
        .debug_selector(|| "drawer-toggle".into())
        .size(px(30.))
        .flex()
        .items_center()
        .justify_center()
        .rounded_full()
        .text_color(theme.muted_foreground)
        .when(open, |button| {
            button
                .bg(theme.secondary_hover)
                .text_color(theme.foreground)
        })
        .hover(|button| button.bg(theme.secondary_hover))
        .cursor_pointer()
        .tooltip(|window, cx| Tooltip::new("Toggle tools drawer").build(window, cx))
        .child(Icon::new(IconName::PanelRight).size(px(17.)))
        .on_click(move |_, window, cx| {
                    cx.defer(crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed);
            dock.update(cx, |dock, cx| {
                dock.toggle_dock(DockPlacement::Right, window, cx)
            })
        })
}

/// The old app's icon for each drawer pane; a pane it didn't have gets a dot.
fn icon(name: &str) -> IconName {
    match name {
        FILES => IconName::Files,
        SOURCE_CONTROL => IconName::GitBranch,
        AGENTS => IconName::Bot,
        TASKS => IconName::ListChecks,
        WORKTREES => IconName::GitFork,
        RUN => IconName::CirclePlay,
        CONTEXT => IconName::Gauge,
        // Lucide's history icon, under the kit's name for it.
        HISTORY => IconName::RotateCcwClock,
        _ => IconName::Dot,
    }
}
