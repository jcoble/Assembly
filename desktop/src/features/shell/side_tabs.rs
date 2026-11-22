//! The sessions column's tabs (Sessions, Files, Search) as the look's segmented control: an inset track with an
//! outline, the shown pane's segment in text-1 on the selected fill with the selected line, the others in text-2.
//! They're drawn here rather than with the kit's tab bar, whose selected tab is fixed to the accent colour. Selection
//! and pane drag/drop stay owned by the dock.

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_PILL, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::ExposedElement as _;
use crate::core::ui::access::control_ids::side_tab;
use gpui_kit::component::dock::{DragPanel, TabGroupContext};
use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// A segment's height, the mockup's `.seg button`.
const SEGMENT_HEIGHT: Pixels = px(30.);
/// The drag chip's size while a segment is dragged.
const DRAG_SIZE: Size<Pixels> = size(px(96.), SEGMENT_HEIGHT);

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

/// One segment: a click shows its pane, a drag carries it out (unless the group may not be rearranged, as the kit
/// asks), and a pane dropped on it joins the group there. `shown` draws it as the selected segment.
fn tab(
    group: &TabGroupContext,
    ix: usize,
    name: &'static str,
    shown: bool,
    look: &Look,
    cx: &App,
) -> Stateful<Div> {
    let drag = group
        .is_draggable()
        .then(|| group.drag_panel(ix, cx))
        .flatten();
    let drop_target = cx.theme().tokens.drop_target;
    let (select, drop) = (group.clone(), group.clone());
    let hover = look.fill_hover;
    // A 1px outline on every segment, clear on the unselected ones, so selecting one doesn't shift the row.
    let (text, fill, line) = match shown {
        true => (look.text_1, look.fill_selected, look.line_selected),
        false => (look.text_2, transparent_black(), transparent_black()),
    };
    h_flex()
        .id(("side-tab", ix))
        .selected_in_tree(Role::Tab, side_tab(name), name, shown)
        .debug_selector(move || format!("side-tab-{name}"))
        .h(SEGMENT_HEIGHT)
        .px(SPACE[5])
        .items_center()
        .justify_center()
        .rounded(CORNER_PILL)
        .border_1()
        .border_color(line)
        .bg(fill)
        .text_color(text)
        .text_role(TextRole::Body, cx)
        .font_weight(FontWeight::MEDIUM)
        .cursor_pointer()
        .when(!shown, |tab| tab.hover(move |tab| tab.bg(hover)))
        .child(name)
        .on_click(move |_, window, cx| {
            select.select_tab(ix,window,cx);
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

/// The row's space after its last tab, where a dropped pane joins the group at the end, as on the kit's tab bar: one
/// from another group joins behind the shown one, and one from this group moves to the last place.
fn end(group: &TabGroupContext, cx: &App) -> Stateful<Div> {
    let drop_target = cx.theme().tokens.drop_target;
    let (group, last) = (group.clone(), group.panels().len() - 1);
    div()
        .id("side-tabs-end")
        .h_full()
        .flex_1()
        .min_w(SPACE[7])
        .when(group.is_droppable(), |end| {
            end.drag_over::<DragPanel>(move |end, _, _, _| end.bg(drop_target))
                .on_drop(move |drag: &DragPanel, window, cx| {
                    let ix = (drag.source() == group.node()).then_some(last);
                    group.drop_panel(drag.clone(), ix, false, window, cx);
                    crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
                })
        })
}

/// The row: the segmented control's inset, outlined track holding one segment per visible pane, then the space a
/// dropped pane joins at the end.
pub(super) fn chips(group: &TabGroupContext, _: &mut Window, cx: &mut App) -> AnyElement {
    let tabs = tabs(group, cx);
    if tabs.is_empty() {
        return Empty.into_any_element();
    }
    let look = Look::of(cx);
    // Sized to its segments rather than the column, so the space after them stays a drop target.
    let track = h_flex()
        .flex_none()
        .p(SPACE[0])
        .gap(SPACE[0])
        .rounded(CORNER_PILL)
        .bg(look.inset)
        .border_1()
        .border_color(look.line)
        .children(
            tabs.into_iter()
                .map(|(ix, name, shown)| tab(group, ix, name, shown, &look, cx)),
        );
    h_flex()
        .id("side-tabs")
        .pt(SPACE[4])
        .px(SPACE[4])
        .child(track)
        .child(end(group, cx))
        .into_any_element()
}
