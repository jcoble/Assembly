//! The sessions column uses the kit's outline tabs. Selection and pane drag/drop stay owned by the dock.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::side_tab;
use gpui_kit::component::dock::{DragPanel, TabGroupContext};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::{ActiveTheme as _, Sizable as _};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

const BAR_HEIGHT: Pixels = px(30.);

/// The group's visible tabs: each one's index, its name, and whether it's the one shown. The shown one is the group's
/// active panel, as the kit marks it: a hidden active panel falls back to the first visible one.
fn tabs(group: &TabGroupContext, cx: &App) -> Vec<(usize, &'static str, bool)> {
    let shown = group.active_panel().map(|panel| panel.panel_id(cx));
    group
        .panels()
        .iter()
        .enumerate()
        .filter(|(_, panel)| panel.visible(cx))
        .map(|(ix, panel)| {
            let id = panel.panel_id(cx);
            (ix, panel.panel_name(cx), Some(id) == shown)
        })
        .collect()
}

/// One tab's root: a click shows its pane, a drag carries it out (unless the group may not be rearranged, as the kit
/// asks), and a pane dropped on it joins the group there.
fn tab(group: &TabGroupContext, ix: usize, name: &'static str, cx: &App) -> Tab {
    let drag = group
        .is_draggable()
        .then(|| group.drag_panel(ix, cx))
        .flatten();
    let drop_target = cx.theme().tokens.drop_target;
    let (select, drop) = (group.clone(), group.clone());
    Tab::new()
        .label(name)
        .control(side_tab(name), name)
        .debug_selector(move || format!("side-tab-{name}"))
        .on_click(move |_, window, cx| {
            select.select_tab(ix,window,cx);
            crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        })
        .when_some(drag, |tab, drag| {
            tab.on_drag(drag, move |drag, offset, _, cx| {
                cx.stop_propagation();
                drag.set_drag_offset(offset);
                drag.set_preview_size(size(px(96.), BAR_HEIGHT));
                cx.new(|_| Preview(name))
            })
        })
        .when(group.is_droppable(), |tab| {
            tab.drag_over::<DragPanel>(move |tab, _, _, _| tab.bg(drop_target))
                .on_drop(move |drag: &DragPanel, window, cx| {
                    drop.drop_panel(drag.clone(), Some(ix), true, window, cx);
                    crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
                })
        })
}

/// The row's space after its last tab, where a dropped pane joins the group at the end, as on the kit's tab bar: one
/// from another group joins behind the shown one, and one from this group moves to the last place.
fn end(group: &TabGroupContext, cx: &App) -> Stateful<Div> {
    let drop_target = cx.theme().tokens.drop_target;
    let (group, last) = (group.clone(), group.panels().len() - 1);
    div()
        .id("side-tabs-end")
        .h_full()
        .flex_1()
        .min_w_4()
        .when(group.is_droppable(), |end| {
            end.drag_over::<DragPanel>(move |end, _, _, _| end.bg(drop_target))
                .on_drop(move |drag: &DragPanel, window, cx| {
                    let ix = (drag.source() == group.node()).then_some(last);
                    group.drop_panel(drag.clone(), ix, false, window, cx);
                    crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
                })
        })
}

/// What follows the cursor while a side tab is dragged: its name on a chip.
struct Preview(&'static str);

impl Render for Preview {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .h(BAR_HEIGHT)
            .px_3()
            .flex()
            .items_center()
            .rounded(px(6.))
            .bg(cx.theme().secondary)
            .text_color(cx.theme().foreground)
            .text_role(TextRole::Body, cx)
            .child(self.0)
    }
}

/// The kit supplies the outline shape, selected state, hover, keyboard navigation and horizontal overflow.
pub(super) fn chips(group: &TabGroupContext, _: &mut Window, cx: &mut App) -> AnyElement {
    let tabs = tabs(group, cx);
    if tabs.is_empty() {
        return Empty.into_any_element();
    }
    let selected = tabs.iter().position(|(_, _, shown)| *shown).unwrap_or(0);
    TabBar::new("side-tabs")
        .outline()
        .small()
        .h(BAR_HEIGHT)
        .px_2()
        .selected_index(selected)
        .children(
            tabs.into_iter()
                .map(|(ix, name, _)| tab(group, ix, name, cx)),
        )
        // The kit mounts its trailing space only when a suffix or overflow menu is present.
        .suffix(Empty)
        .last_empty_space(end(group, cx))
        .into_any_element()
}
