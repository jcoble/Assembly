//! The drawer's tool capsule, overflow menu and close button, as RightPanelTabs.svelte drew them: the permanent tools,
//! then the one overflow tool last picked, which the next pick replaces. Only the close button shuts the drawer. The dock
//! remains the authority for the shown pane and its saved layout.

use crate::core::ui::access::control_ids::{drawer as ids, drawer_pane, topbar};
use crate::core::ui::access::{Control as _, ExposedElement as _};
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

const PILL_W: Pixels = px(28.);
const PILL_H: Pixels = px(24.);
const CONTROL: Pixels = px(28.);
const GLYPH: Pixels = px(14.);

/// The overflow tool in the capsule. Memory only, as the old app kept it: after a relaunch it follows the shown pane.
#[derive(Default)]
struct LastExtra(Option<&'static str>);
impl Global for LastExtra {}

/// What pressing a drawer pane's button does.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Press {
    /// The drawer is shut: open it on this pane.
    Open,
    /// The drawer is open: show this pane, which leaves it open even if it's the one shown.
    Show,
}

/// What a press does, from whether the drawer is open.
pub fn press(open: bool) -> Press {
    if open { Press::Show } else { Press::Open }
}

/// The overflow tool the capsule keeps: the one the drawer shows, or else the last one it showed.
pub fn extra_after(
    last: Option<&'static str>,
    shown: Option<&'static str>,
) -> Option<&'static str> {
    shown.filter(|name| !primary(name)).or(last)
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
        let shown = panes.iter().find(|pane| pane.2).map(|pane| pane.1);
        let last = cx.default_global::<LastExtra>().0;
        let extra = extra_after(last, shown);
        if extra != last {
            cx.set_global(LastExtra(extra));
        }
        let theme = cx.theme();
        let (lit, color, foreground, capsule) = (
            theme.secondary_hover,
            theme.muted_foreground,
            theme.foreground,
            theme.secondary,
        );
        let button = |name: &'static str, tree_id: String, glyph: IconName, on: bool| {
            div()
                .id(name)
                .selected_in_tree(Role::Button, tree_id, name, on)
                .debug_selector(move || format!("drawer-button-{name}"))
                .w(PILL_W)
                .h(PILL_H)
                .flex()
                .items_center()
                .justify_center()
                .rounded_full()
                .text_color(color)
                .cursor_pointer()
                .when(on, |button| button.bg(lit).text_color(foreground))
                .hover(|button| button.bg(lit))
                .tooltip(move |window, cx| Tooltip::new(name).build(window, cx))
                .child(Icon::new(glyph).size(GLYPH))
        };
        let overflow: Vec<_> = panes
            .iter()
            .copied()
            .filter(|(_, name, _)| !primary(name))
            .collect();
        let panes = panes
            .into_iter()
            .filter(|(_, name, _)| primary(name) || extra == Some(*name))
            .map(|(id, name, shown)| {
                let dock = self.dock.clone();
                button(name, drawer_pane(name), icon(name), open && shown).on_click(move |_, window, cx| {
                    cx.defer(crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed);
                    dock.update(cx, |dock, cx| match press(open) {
                        Press::Open => {
                            dock.select_panel(id, window, cx);
                            dock.toggle_dock(DockPlacement::Right, window, cx);
                        }
                        Press::Show => dock.select_panel(id, window, cx),
                    })
                })
            });
        let dock = self.dock.clone();
        let menu = Button::new("drawer-overflow")
            .control(ids::MORE_PANELS, "More panels")
            .ghost()
            .icon(Icon::new(IconName::Ellipsis).size(GLYPH))
            .w(CONTROL)
            .h(CONTROL)
            .tooltip("More panels")
            .dropdown_menu_with_anchor(Anchor::TopRight, move |menu, _, _| {
                let menu =
                    overflow
                        .iter()
                        .fold(menu.min_w(px(176.)), |menu, &(id, name, _)| {
                            let dock = dock.clone();
                            menu.item(
                                PopupMenuItem::new(name)
                                    .icon(icon(name))
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
        let close = button("Close side panel", ids::CLOSE.into(), IconName::X, false).w(CONTROL).h(CONTROL).rounded_md().on_click(move |_, window, cx| {
                    cx.defer(crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed);
            dock.update(cx, |dock, cx| {
                dock.toggle_dock(DockPlacement::Right, window, cx)
            })
        });
        h_flex()
            .h(px(44.))
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
            .child(
                div()
                    .debug_selector(|| "drawer-overflow".into())
                    .child(menu),
            )
            .child(close)
    }
}

fn primary(name: &str) -> bool {
    matches!(name, SOURCE_CONTROL | AGENTS | TASKS)
}

/// The chrome keeps a single, discoverable control while the drawer is closed.
pub fn toggle(dock: Entity<DockArea>, cx: &App) -> impl IntoElement {
    let open = dock.read(cx).is_dock_open(DockPlacement::Right);
    let theme = cx.theme();
    div()
        .id("drawer-toggle")
        .selected_in_tree(
            Role::Button,
            topbar::DRAWER_TOGGLE,
            "Toggle tools drawer",
            open,
        )
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
            cx.defer(
                crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed,
            );
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
