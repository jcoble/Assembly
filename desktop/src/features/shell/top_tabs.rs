//! Content tabs in the window chrome, backed by the dock's existing panels and layout. Selection, closing and
//! movement still go through the dock; this bar keeps no second list of open tabs.

use std::sync::Arc;

use gpui_kit::assets::IconName;
use gpui_kit::component::dock::{
    BasePanelView, ClosePanel, DockArea, DockPlacement, DragPanel, InsertTarget, NodeId, PaneRef,
    PanelHandle,
};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

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
    let selected = tabs
        .iter()
        .position(|tab| tab.shown && tab.panel.focus_handle(cx).contains_focused(window, cx))
        .or_else(|| tabs.iter().position(|tab| tab.shown))
        .unwrap_or(0);
    let target = cx.theme().tokens.drop_target;
    let muted = cx.theme().muted_foreground;
    let hover = cx.theme().secondary_hover;
    let chips = tabs
        .into_iter()
        .map(|tab| {
            let id = tab.panel.panel_id(cx);
            let name = tab.panel.panel_name(cx);
            let title = PanelHandle::of(&tab.panel)
                .map(|handle| handle.title(window, cx))
                .unwrap_or_else(|| SharedString::from(name).into_any_element());
            let select = dock.clone();
            let close = dock.clone();
            let drop = dock.clone();
            let panel = tab.panel.clone();
            let chip = Tab::new()
                .debug_selector(move || format!("top-tab-{name}"))
                .aria_label(name)
                .prefix(Icon::new(icon(name)).size(px(16.)))
                .child(title)
                .on_click(move |_, window, cx| {
                    select.update(cx, |dock, cx| dock.select_panel(id, window, cx));
                })
                .when(tab.panel.closable(cx), |chip| {
                    chip.suffix(
                        div()
                            .id(("close-content-tab", id.as_u64()))
                            .size(px(22.))
                            .rounded(px(6.))
                            .flex()
                            .items_center()
                            .justify_center()
                            .text_color(muted)
                            .hover(|close| close.bg(hover))
                            .child(Icon::new(IconName::X).size(px(13.)))
                            .on_click(move |_, window, cx| {
                                cx.stop_propagation();
                                close.update(cx, |dock, cx| dock.select_panel(id, window, cx));
                                panel.focus_handle(cx).focus(window, cx);
                                // Keep the kit's close path and its panel/group refusal rules.
                                window.dispatch_action(Box::new(ClosePanel), cx);
                            }),
                    )
                });
            let chip = chip.when(tab.draggable, |chip| {
                chip.on_drag(DragPanel::new(id, tab.node), move |drag, offset, _, cx| {
                    cx.stop_propagation();
                    drag.set_drag_offset(offset);
                    drag.set_preview_size(size(px(140.), px(32.)));
                    cx.new(|_| Preview(name))
                })
            });
            chip.drag_over::<DragPanel>(move |chip, _, _, _| chip.bg(target))
                .on_drop(move |drag: &DragPanel, window, cx| {
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
        })
        .collect::<Vec<_>>();
    TabBar::new("chrome-content-tabs")
        .folder()
        .max_width(px(220.))
        .selected_index(selected)
        .children(chips)
        .flex_1()
        .min_w_0()
        .into_any_element()
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

struct Preview(&'static str);

impl Render for Preview {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        h_flex()
            .h(px(32.))
            .px_3()
            .rounded(px(8.))
            .bg(cx.theme().secondary)
            .child(self.0)
    }
}
