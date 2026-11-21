//! Content tabs in the window chrome, backed by the dock's existing panels and layout. Selection, closing and
//! movement still go through the dock; this bar keeps no second list of open tabs.
//!
//! Each tab is a pill on the glass, as the rule sheet's top bar draws them: inactive ones in on-glass text-2, the
//! active one in on-glass text-1 with the selected fill and outline. They're drawn here rather than with the kit's
//! tab bar, which fixes its own tab colours.

use std::sync::Arc;

use gpui_kit::assets::IconName;
use gpui_kit::component::dock::{
    BasePanelView, ClosePanel, DockArea, DockPlacement, DragPanel, InsertTarget, NodeId, PaneRef,
    PanelHandle,
};
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{BUTTON_HEIGHT, CORNER_PILL, CORNER_TAG, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::ExposedElement as _;
use crate::core::ui::access::control_ids::content_tab;
use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;

/// A tab's widest, so a long title shortens rather than pushing the others off the bar.
const TAB_MAX_WIDTH: Pixels = px(220.);
/// The close x's square, inside a closable tab.
const CLOSE_SIZE: Pixels = px(20.);
/// The x itself, and the tab's own icon.
const CLOSE_GLYPH: Pixels = px(14.);
const TAB_GLYPH: Pixels = px(16.);

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
    let look = Look::of(cx);
    let target: Hsla = cx.theme().tokens.drop_target.into();
    let chips = tabs
        .into_iter()
        .enumerate()
        .map(|(position, tab)| chip(tab, position == selected, &dock, &look, target, window, cx))
        .collect::<Vec<_>>();
    h_flex()
        .id("chrome-content-tabs")
        .h_full()
        .min_w_0()
        .flex_shrink(1.)
        .gap(SPACE[1])
        .overflow_x_scroll()
        .children(chips)
        .into_any_element()
}

/// One tab: a click shows its pane, its x closes it through the kit's close path, a drag carries it to another
/// group, and a tab dropped on it lands in its place.
fn chip(
    tab: ContentTab,
    active: bool,
    dock: &Entity<DockArea>,
    look: &Look,
    target: Hsla,
    window: &mut Window,
    cx: &mut App,
) -> Stateful<Div> {
    let id = tab.panel.panel_id(cx);
    let name = tab.panel.panel_name(cx);
    let title = PanelHandle::of(&tab.panel)
        .map(|handle| handle.title(window, cx))
        .unwrap_or_else(|| SharedString::from(name).into_any_element());
    let closable = tab.panel.closable(cx);
    let (select, close, drop) = (dock.clone(), dock.clone(), dock.clone());
    let panel = tab.panel.clone();
    // A 1px outline on every tab, clear on the inactive ones, so activating one doesn't shift the row.
    let (text, fill, line) = match active {
        true => (
            look.on_glass_text_1,
            look.on_glass_fill_selected,
            look.on_glass_line_selected,
        ),
        false => (
            look.on_glass_text_2,
            transparent_black(),
            transparent_black(),
        ),
    };
    let hover = look.fill_hover;
    let icon_colour = look.on_glass_icon_3;
    h_flex()
        .id(("content-tab", id.as_u64()))
        .selected_in_tree(Role::Tab, content_tab(name), name, active)
        .debug_selector(move || format!("top-tab-{name}"))
        .flex_shrink_0()
        .max_w(TAB_MAX_WIDTH)
        .h(BUTTON_HEIGHT)
        .pl(SPACE[5])
        .pr(if closable { SPACE[2] } else { SPACE[6] })
        .gap(SPACE[3])
        .rounded(CORNER_PILL)
        .border_1()
        .border_color(line)
        .bg(fill)
        .text_color(text)
        .text_role(TextRole::Body, cx)
        .when(active, |chip| chip.font_weight(FontWeight::MEDIUM))
        .cursor_pointer()
        .when(!active, |chip| chip.hover(move |chip| chip.bg(hover)))
        .child(Icon::new(icon(name)).size(TAB_GLYPH))
        .child(
            div()
                .min_w_0()
                .overflow_hidden()
                .whitespace_nowrap()
                .child(title),
        )
        .on_click(move |_, window, cx| {
            cx.defer(WorkspaceCoordinator::changed);
            select.update(cx, |dock, cx| dock.select_panel(id, window, cx));
        })
        .when(closable, |chip| {
            chip.child(
                div()
                    .id(("close-content-tab", id.as_u64()))
                    .flex_none()
                    .size(CLOSE_SIZE)
                    .rounded(CORNER_TAG)
                    .flex()
                    .items_center()
                    .justify_center()
                    .text_color(icon_colour)
                    .hover(move |close| close.bg(hover))
                    .child(Icon::new(IconName::X).size(CLOSE_GLYPH))
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
                drag.set_preview_size(size(px(140.), BUTTON_HEIGHT));
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

fn icon(name: &str) -> IconName {
    match name {
        "Chat" => IconName::MessageCircle,
        "Changes" => IconName::GitBranch,
        "Build Board" => IconName::LayoutDashboard,
        "Settings" => IconName::Settings,
        _ => IconName::File,
    }
}
