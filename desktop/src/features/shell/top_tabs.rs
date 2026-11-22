//! Content tabs in the window chrome, backed by the dock's existing panels and layout. Selection, closing and
//! movement still go through the dock; this bar keeps no second list of open tabs.
//!
//! The kit's tab bar in its segmented variant, so the strip keeps the kit's tab-list role, wheel scrolling, reveal of
//! a clipped tab and keyboard focus; each tab's close is a named kit icon button. Called by the top bar.

use std::sync::Arc;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{
    BasePanelView, ClosePanel, DockArea, DockPlacement, DragPanel, InsertTarget, NodeId, PaneRef,
    PanelHandle,
};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::theme::metrics::BUTTON_HEIGHT;
use crate::core::ui::access::Control as _;
use crate::core::ui::access::Named as _;
use crate::core::ui::access::control_ids::content_tab;
use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;

/// A tab's widest, so a long title shortens rather than pushing the others off the bar.
const TAB_MAX_WIDTH: Pixels = px(220.);
/// The tab's own icon and the close x.
const TAB_GLYPH: Pixels = px(16.);
const CLOSE_GLYPH: Pixels = px(14.);
/// What follows the pointer while a tab is dragged.
const DRAG_SIZE: Size<Pixels> = size(px(140.), BUTTON_HEIGHT);

struct ContentTab {
    panel: Arc<dyn BasePanelView>,
    node: NodeId,
    ix: usize,
    shown: bool,
    draggable: bool,
}

/// Resolve current panels every render, so restored, closed and dragged tabs use the same source of truth.
pub(super) fn tabs(dock: Entity<DockArea>, window: &mut Window, cx: &mut App) -> AnyElement {
    let area = dock.read(cx);
    let Some(tree) = area.layout(DockPlacement::Center) else {
        return Empty.into_any_element();
    };
    // Every visible panel in the center, in its tab group's order.
    let mut tabs = Vec::new();
    tree.root().walk(&mut |node| {
        if let PaneRef::Tabs { panels, active_ix } = node.kind() {
            for (ix, id) in panels.iter().enumerate() {
                if let Some(panel) = area.panel(*id).filter(|panel| panel.visible(cx)) {
                    tabs.push(ContentTab {
                        panel: panel.clone(),
                        node: node.id(),
                        ix,
                        shown: ix == active_ix,
                        draggable: !area.is_locked() && !area.is_zoomed() && panels.len() > 1,
                    });
                }
            }
        }
    });
    // The active tab: the shown one that holds the keyboard, else the first shown one.
    let selected = tabs
        .iter()
        .position(|tab| tab.shown && tab.panel.focus_handle(cx).contains_focused(window, cx))
        .or_else(|| tabs.iter().position(|tab| tab.shown))
        .unwrap_or(0);
    let chips = tabs
        .into_iter()
        .map(|tab| tab_of(tab, &dock, window, cx))
        .collect::<Vec<_>>();
    TabBar::new("chrome-content-tabs")
        .segmented()
        .max_width(TAB_MAX_WIDTH)
        .selected_index(selected)
        .children(chips)
        // Sized to its tabs, and shrinking (the kit then scrolls it) when the bar runs out of room.
        .min_w_0()
        .into_any_element()
}

/// One tab: a click shows its pane, its x closes it through the kit's close path, a drag carries it to another
/// group, and a tab dropped on it lands in its place.
fn tab_of(tab: ContentTab, dock: &Entity<DockArea>, window: &mut Window, cx: &mut App) -> Tab {
    let id = tab.panel.panel_id(cx);
    let name = tab.panel.panel_name(cx);
    // What the tab is called to a screen reader: the pane's own short name when it has one (a file tab's file
    // name), else its kind ("Chat", "Changes").
    let label = PanelHandle::of(&tab.panel)
        .and_then(|handle| handle.tab_name(cx))
        .unwrap_or_else(|| SharedString::from(name));
    let title = PanelHandle::of(&tab.panel)
        .map(|handle| handle.title(window, cx))
        .unwrap_or_else(|| SharedString::from(name).into_any_element());
    let target = cx.theme().tokens.drop_target;
    let (select, close, drop) = (dock.clone(), dock.clone(), dock.clone());
    let panel = tab.panel.clone();
    Tab::new()
        .debug_selector(move || format!("top-tab-{name}"))
        .control(content_tab(name), label.clone())
        .prefix(Icon::new(icon(name)).size(TAB_GLYPH))
        .child(title)
        .on_click(move |_, window, cx| {
            cx.defer(WorkspaceCoordinator::changed);
            select.update(cx, |dock, cx| dock.select_panel(id, window, cx));
        })
        .when(tab.panel.closable(cx), |chip| {
            chip.suffix(
                Button::new(("close-content-tab", id.as_u64()))
                    .named(format!("Close {label}"))
                    .ghost()
                    .xsmall()
                    .icon(Icon::new(IconName::X).size(CLOSE_GLYPH))
                    .on_click(move |_, window, cx| {
                        cx.defer(WorkspaceCoordinator::changed);
                        cx.stop_propagation();
                        close.update(cx, |dock, cx| dock.select_panel(id, window, cx));
                        panel.focus_handle(cx).focus(window, cx);
                        // Keep the kit's close path and its panel/group refusal rules.
                        window.dispatch_action(Box::new(ClosePanel), cx);
                    }),
            )
        })
        .when(tab.draggable, |chip| {
            chip.on_drag(DragPanel::new(id, tab.node), move |drag, offset, _, cx| {
                cx.stop_propagation();
                drag.set_drag_offset(offset);
                drag.set_preview_size(DRAG_SIZE);
                cx.new(|_| super::drag_chip::DragChip(name))
            })
        })
        .drag_over::<DragPanel>(move |chip, _, _, _| chip.bg(target))
        .on_drop(move |drag: &DragPanel, window, cx| {
            cx.defer(WorkspaceCoordinator::changed);
            drop.update(cx, |dock, cx| {
                if !dock.is_locked() && !dock.is_zoomed() {
                    dock.move_panel(
                        drag.panel(),
                        InsertTarget::Tabs {
                            node: tab.node,
                            ix: Some(tab.ix),
                            activate: true,
                        },
                        window,
                        cx,
                    );
                }
            });
        })
}

/// The icon at the front of a tab, by its pane.
fn icon(name: &str) -> IconName {
    match name {
        "Chat" => IconName::MessageCircle,
        "Changes" => IconName::GitBranch,
        "Build Board" => IconName::LayoutDashboard,
        "Settings" => IconName::Settings,
        _ => IconName::File,
    }
}
