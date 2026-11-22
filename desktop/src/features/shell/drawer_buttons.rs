//! The drawer's tool capsule (icon segments on the kit's button group, components/icon_segments.rs), overflow menu and close button,
//! as RightPanelTabs.svelte arranged them: the permanent tools, then the one overflow tool last picked, which the next
//! pick replaces. Only the close button shuts the drawer. The dock
//! remains the authority for the shown pane and its saved layout.

use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::{drawer as ids, drawer_pane, topbar};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{DockArea, DockPlacement, PaneRef, PanelId};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{Icon, h_flex};
use gpui_kit::*;

use super::default_layout::{
    AGENTS, CONTEXT, FILES, HISTORY, RUN, SOURCE_CONTROL, TASKS, WORKTREES,
};
use super::terminal_toggle::menu_item;
use crate::components::glass_button::glass_icon_button;
use crate::components::icon_segments::{icon_segments, segment};

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
        let open = area.is_dock_open(DockPlacement::Right);
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
        let overflow: Vec<_> = panes
            .iter()
            .copied()
            .filter(|(_, name, _)| !primary(name))
            .collect();
        // The capsule's panes: the permanent tools, then the one overflow tool kept. While the drawer is open, the
        // shown one is the bar's selected segment.
        let panes: Vec<_> = panes
            .into_iter()
            .filter(|(_, name, _)| primary(name) || extra == Some(*name))
            .collect();
        let tabs = panes.into_iter().map(|(id, name, shown)| {
            let dock = self.dock.clone();
            segment(SharedString::from(format!("drawer-pane-{name}")), icon(name), open && shown, cx)
                .control(drawer_pane(name), name)
                .debug_selector(move || format!("drawer-button-{name}"))
                .tooltip(name)
                .on_click(move |_, window, cx| {
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
        let capsule = icon_segments("drawer-panes", cx).children(tabs);
        let dock = self.dock.clone();
        let menu = Button::new("drawer-overflow")
            .control(ids::MORE_PANELS, "More panels")
            .ghost()
            .icon(Icon::new(IconName::Ellipsis).size(GLYPH))
            .w(CONTROL)
            .h(CONTROL)
            .tooltip("More panels")
            .dropdown_menu_with_anchor(Anchor::TopRight, move |menu, _, cx| {
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
                menu.separator().item(menu_item(dock.clone(), cx))
            });
        let dock = self.dock.clone();
        let close = Button::new("drawer-close")
            .control(ids::CLOSE, "Close side panel")
            .ghost()
            .icon(Icon::new(IconName::X).size(GLYPH))
            .w(CONTROL)
            .h(CONTROL)
            .tooltip("Close side panel")
            .on_click(move |_, window, cx| {
                cx.defer(crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed);
                dock.update(cx, |dock, cx| {
                    dock.toggle_dock(DockPlacement::Right, window, cx)
                })
            });
        h_flex()
            .h(px(44.))
            .px_2()
            .gap_1()
            .child(capsule)
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

/// The chrome keeps a single, discoverable control while the drawer is closed: a glass icon button, picked while the
/// drawer is open.
pub fn toggle(dock: Entity<DockArea>, cx: &App) -> impl IntoElement {
    let open = dock.read(cx).is_dock_open(DockPlacement::Right);
    div().debug_selector(|| "drawer-toggle".into()).child(
        glass_icon_button("drawer-toggle", IconName::PanelRight, open, cx)
            .control(topbar::DRAWER_TOGGLE, "Toggle tools drawer")
            // A toggle in the tree: whether the drawer is open.
            .toggled(open)
            .tooltip("Toggle tools drawer")
            .on_click(move |_, window, cx| {
                cx.defer(
                    crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed,
                );
                dock.update(cx, |dock, cx| {
                    dock.toggle_dock(DockPlacement::Right, window, cx)
                })
            }),
    )
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
