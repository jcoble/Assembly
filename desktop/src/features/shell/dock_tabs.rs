//! The docks' tabs, drawn as the old app's drawer drew them (RightPanelTabs.svelte; Jesse 2026-10-27: "make these more
//! like the ones in the old app, same with left side", then "make the left side not completely different from the
//! right. Same with the bottom tabs"). Each dock's tabs are one rounded strip of pills, the shown one lit: the drawer's
//! are the old app's icons with the name on hover, the sessions column's and the bottom dock's are their names. The
//! strip is 30 px high, as the chat's title is, so the column tops line up. A click shows that tab's pane, and on the
//! closed bottom dock's strip it opens the dock too; the bottom dock keeps its open and close button. A tab drags its
//! pane elsewhere and takes one dropped on it, as the kit's do, and a strip too narrow for its tabs scrolls.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{DockArea, DockPlacement, DragPanel, TabGroupContext};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::default_layout::{AGENTS, CONTEXT, HISTORY, RUN, SOURCE_CONTROL, TASKS, WORKTREES};

const BAR_HEIGHT: Pixels = px(30.);
const PILL_HEIGHT: Pixels = px(24.);

/// The group's visible tabs: each one's index, its name, and whether it's the one shown. The shown one is the group's
/// active panel, as the kit marks it: a hidden active panel falls back to the first visible one. A collapsed group (the
/// closed bottom dock's strip) shows none as shown.
fn tabs(group: &TabGroupContext, cx: &App) -> Vec<(usize, &'static str, bool)> {
    let shown = group
        .active_panel()
        .filter(|_| !group.is_collapsed())
        .map(|panel| panel.panel_id(cx));
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

/// One tab's root: a click shows its pane (and opens a collapsed dock), a drag carries it out (unless the group may not
/// be rearranged, as the kit asks, or is collapsed), and a pane dropped on it joins the group there.
fn tab(
    group: &TabGroupContext,
    ix: usize,
    name: &'static str,
    opens: Option<(WeakEntity<DockArea>, DockPlacement)>,
    cx: &App,
) -> Stateful<Div> {
    let drag = (group.is_draggable() && !group.is_collapsed())
        .then(|| group.drag_panel(ix, cx))
        .flatten();
    let drop_target = cx.theme().tokens.drop_target;
    let (select, drop) = (group.clone(), group.clone());
    div()
        .id(name)
        .debug_selector(move || format!("tab-pill-{name}"))
        .flex_none()
        .flex()
        .items_center()
        .cursor_pointer()
        .on_click(move |_, window, cx| {
            select.select_tab(ix, window, cx);
            if let Some((area, dock)) = &opens {
                toggle(area, *dock, window, cx);
            }
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
                })
        })
}

fn toggle(area: &WeakEntity<DockArea>, dock: DockPlacement, window: &mut Window, cx: &mut App) {
    _ = area.update(cx, |area, cx| area.toggle_dock(dock, window, cx));
}

/// The strip's space after its last tab, where a dropped pane joins the group at the end, as on the kit's tab bar: one
/// from another group joins behind the shown one, and one from this group moves to the last place.
fn end(group: &TabGroupContext, cx: &App) -> Stateful<Div> {
    let drop_target = cx.theme().tokens.drop_target;
    let (group, last) = (group.clone(), group.panels().len() - 1);
    div()
        .id("dock-tabs-end")
        .h_full()
        .flex_1()
        .min_w_4()
        .when(group.is_droppable(), |end| {
            end.drag_over::<DragPanel>(move |end, _, _, _| end.bg(drop_target))
                .on_drop(move |drag: &DragPanel, window, cx| {
                    let ix = (drag.source() == group.node()).then_some(last);
                    group.drop_panel(drag.clone(), ix, false, window, cx);
                })
        })
}

/// What follows the cursor while a tab is dragged: its name on a pill.
struct Preview(&'static str);

impl Render for Preview {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .h(PILL_HEIGHT)
            .px(px(10.))
            .flex()
            .items_center()
            .rounded_full()
            .bg(cx.theme().muted)
            .text_color(cx.theme().foreground)
            .text_size(px(12.))
            .child(self.0)
    }
}

/// The bottom dock's open and close button, as the kit's tab bar has it.
fn bottom_toggle(area: &WeakEntity<DockArea>, cx: &App) -> Option<Button> {
    let open = area.upgrade()?.read(cx).is_dock_open(DockPlacement::Bottom);
    let area = area.clone();
    Some(
        Button::new("bottom-dock-toggle")
            .debug_selector(|| "bottom-dock-toggle".to_string())
            .icon(match open {
                true => IconName::PanelBottom,
                false => IconName::PanelBottomOpen,
            })
            .xsmall()
            .ghost()
            .tab_stop(false)
            .on_click(move |_, window, cx| toggle(&area, DockPlacement::Bottom, window, cx)),
    )
}

/// A dock's tabs as one rounded `muted` strip of pills, the shown one lit at 16% `foreground`: icon pills 28 wide in
/// the drawer, name pills elsewhere.
pub(super) fn pills(
    group: &TabGroupContext,
    dock: DockPlacement,
    area: &WeakEntity<DockArea>,
    cx: &mut App,
) -> AnyElement {
    let tabs = tabs(group, cx);
    if tabs.is_empty() {
        return Empty.into_any_element();
    }
    let icons = dock == DockPlacement::Right;
    let opens = group.is_collapsed().then(|| (area.clone(), dock));
    let toggle = (dock == DockPlacement::Bottom)
        .then(|| bottom_toggle(area, cx))
        .flatten();
    let theme = cx.theme();
    let (foreground, muted, muted_foreground) =
        (theme.foreground, theme.muted, theme.muted_foreground);
    h_flex()
        .id("dock-tabs")
        .h(BAR_HEIGHT)
        .min_w_0()
        .overflow_x_scroll()
        .px_2()
        .gap_2()
        .children(toggle)
        .child(
            h_flex()
                .flex_none()
                .rounded_full()
                .bg(muted)
                .p(px(3.))
                .gap(px(2.))
                .children(tabs.into_iter().map(|(ix, name, shown)| {
                    tab(group, ix, name, opens.clone(), cx)
                        .h(PILL_HEIGHT)
                        .justify_center()
                        .rounded_full()
                        .text_color(muted_foreground)
                        .map(|pill| match icons {
                            true => pill
                                .w(px(28.))
                                .tooltip(move |window, cx| Tooltip::new(name).build(window, cx))
                                .child(Icon::new(icon(name)).size(px(14.))),
                            false => pill.px(px(10.)).text_size(px(12.)).child(name),
                        })
                        .map(|pill| match shown {
                            true => pill.bg(foreground.opacity(0.16)).text_color(foreground),
                            false => pill.hover(|pill| pill.bg(foreground.opacity(0.06))),
                        })
                })),
        )
        .child(end(group, cx))
        .into_any_element()
}

/// The old app's icon for each drawer pane; a pane it didn't have gets a dot.
fn icon(name: &str) -> IconName {
    match name {
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
