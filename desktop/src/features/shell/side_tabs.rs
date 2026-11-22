//! The sessions column's tabs (Sessions, Files, Search) as the kit's segmented tab bar across the column, the
//! segments sharing the room past their names, on the look's capsule track with its outline; when the panes no longer
//! fit, the kit scrolls the strip. Selection and pane drag/drop stay owned by the dock. Called by the dock for the left
//! column's groups.

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_PILL, SPACE};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::side_tab;
use gpui_kit::component::dock::{DragPanel, TabGroupContext};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// How readily the bar shrinks next to the gutter: so little that the gutter gives up nearly all the room it can
/// first. A flex item shrinks by this times its own width, and the gutter's factor is 1.
const BAR_SHRINK: f32 = 0.0001;
/// The drag chip's size while a segment is dragged.
const DRAG_SIZE: Size<Pixels> = size(px(96.), px(30.));

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

/// One segment, the kit's segmented tab: a click shows its pane, a drag carries it out (unless the group may not be
/// rearranged, as the kit asks), and a pane dropped on it joins the group there. The bar marks the shown one.
fn tab(group: &TabGroupContext, ix: usize, name: &'static str, cx: &App) -> Tab {
    let drag = group
        .is_draggable()
        .then(|| group.drag_panel(ix, cx))
        .flatten();
    let drop_target = cx.theme().tokens.drop_target;
    let (select, drop) = (group.clone(), group.clone());
    Tab::new()
        .label(name)
        // Each segment is at least as wide as its name and shares out the room left over, so a crowded column scrolls
        // the strip rather than cutting a name short.
        .flex_grow(1.)
        .control(side_tab(name), name)
        .debug_selector(move || format!("side-tab-{name}"))
        .on_click(move |_, window, cx| {
            select.select_tab(ix, window, cx);
            crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        })
        .when_some(drag, |tab, drag| {
            tab.on_drag(drag, move |drag, offset, _, cx| {
                cx.stop_propagation();
                drag.set_drag_offset(offset);
                drag.set_preview_size(DRAG_SIZE);
                cx.new(|_| super::drag_chip::DragChip(name))
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

/// The column's right gutter after the bar, where a dropped pane joins the group at the end, as on the kit's tab bar:
/// one from another group joins behind the shown one, and one from this group moves to the last place. A little wider
/// than the left margin so it stays easy to drop on, and the first thing to give way, down to a sliver, when the
/// segments' names need the room.
fn end(group: &TabGroupContext, cx: &App) -> Stateful<Div> {
    let drop_target = cx.theme().tokens.drop_target;
    let (group, last) = (group.clone(), group.panels().len() - 1);
    div()
        .id("side-tabs-end")
        .h_full()
        .w(SPACE[9])
        .min_w(SPACE[1])
        .flex_shrink(1.)
        .when(group.is_droppable(), |end| {
            end.drag_over::<DragPanel>(move |end, _, _, _| end.bg(drop_target))
                .on_drop(move |drag: &DragPanel, window, cx| {
                    let ix = (drag.source() == group.node()).then_some(last);
                    group.drop_panel(drag.clone(), ix, false, window, cx);
                    crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
                })
        })
}

/// The row: the kit's segmented bar holding one segment per visible pane, with the look's outline on its track, then
/// the space a dropped pane joins at the end.
pub(super) fn chips(group: &TabGroupContext, _: &mut Window, cx: &mut App) -> AnyElement {
    let tabs = tabs(group, cx);
    if tabs.is_empty() {
        return Empty.into_any_element();
    }
    let look = Look::of(cx);
    let shown = tabs.iter().position(|(_, _, shown)| *shown);
    // Across the column, as the look's `.seg`; the column's right gutter after it stays the drop target. The kit's
    // small size keeps a segment within the header's 30px.
    //
    // The bar starts from its segments' own width, which the layout measures from each name as the font draws it,
    // and grows into any room left over. When the column is short of that width, the gutter gives up its room first
    // (the bar barely shrinks while the gutter can); only once the gutter is down to its sliver does the bar shrink
    // below its names, and then the kit scrolls the strip.
    let bar = TabBar::new("side-tabs-bar")
        .segmented()
        .small()
        .flex_grow(1.)
        .flex_shrink(BAR_SHRINK)
        .min_w_0()
        .rounded(CORNER_PILL)
        .border_1()
        .border_color(look.line)
        .when_some(shown, |bar, shown| bar.selected_index(shown))
        .children(
            tabs.into_iter()
                .map(|(ix, name, _)| tab(group, ix, name, cx)),
        );
    h_flex()
        .id("side-tabs")
        .pt(SPACE[4])
        .pl(SPACE[4])
        .child(bar)
        .child(end(group, cx))
        .into_any_element()
}
