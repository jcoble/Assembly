//! The side docks' tabs, drawn as the old app drew them (Jesse 2026-10-27: "make these more like the ones in the old
//! app, same with left side"). The drawer's are RightPanelTabs.svelte's: one rounded strip of icon pills, the shown one
//! lit, the name on hover. The sessions column had no tabs in the old app, so its are the old top tab row's text chips
//! (TopTabRow.svelte). Both are 30 px high, as the kit's tab bar and the chat's title are, so the column tops line up.
//! A click shows that tab's pane. A tab drags its pane elsewhere and takes one dropped on it, as the kit's do, and a
//! row too narrow for its tabs scrolls, as the old top tab row did.

use gpui_kit::assets::IconName;
use gpui_kit::component::dock::{DragPanel, TabGroupContext};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::default_layout::{AGENTS, CONTEXT, HISTORY, RUN, SOURCE_CONTROL, TASKS, WORKTREES};

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
fn tab(group: &TabGroupContext, ix: usize, name: &'static str, cx: &App) -> Stateful<Div> {
    let drag = group
        .is_draggable()
        .then(|| group.drag_panel(ix, cx))
        .flatten();
    let drop_target = cx.theme().tokens.drop_target;
    let (select, drop) = (group.clone(), group.clone());
    div()
        .id(name)
        .debug_selector(move || format!("side-tab-{name}"))
        .flex_none()
        .flex()
        .items_center()
        .cursor_pointer()
        .on_click(move |_, window, cx| select.select_tab(ix, window, cx))
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
            .text_size(px(13.))
            .child(self.0)
    }
}

/// The sessions column's text chips: 6 px corners, the shown one on `secondary`, the others dimmer until hovered.
pub(super) fn chips(group: &TabGroupContext, _: &mut Window, cx: &mut App) -> AnyElement {
    let tabs = tabs(group, cx);
    if tabs.is_empty() {
        return Empty.into_any_element();
    }
    let theme = cx.theme();
    let (foreground, secondary, accent) = (theme.foreground, theme.secondary, theme.accent);
    h_flex()
        .id("side-tabs")
        .h(BAR_HEIGHT)
        .min_w_0()
        .overflow_x_scroll()
        .px_2()
        .gap_1()
        .children(tabs.into_iter().map(|(ix, name, shown)| {
            tab(group, ix, name, cx)
                .h(BAR_HEIGHT)
                .px_3()
                .rounded(px(6.))
                .text_size(px(13.))
                .child(name)
                .map(|chip| match shown {
                    true => chip.bg(secondary).text_color(foreground),
                    false => chip
                        .text_color(foreground.opacity(0.7))
                        .hover(|chip| chip.bg(accent).text_color(foreground)),
                })
        }))
        .child(end(group, cx))
        .into_any_element()
}

/// The drawer's icon pills, in one rounded `muted` strip: 28 by 24, the shown one lit at 16% `foreground`.
pub(super) fn pills(group: &TabGroupContext, _: &mut Window, cx: &mut App) -> AnyElement {
    let tabs = tabs(group, cx);
    if tabs.is_empty() {
        return Empty.into_any_element();
    }
    let theme = cx.theme();
    let (foreground, muted, muted_foreground) =
        (theme.foreground, theme.muted, theme.muted_foreground);
    h_flex()
        .id("side-tabs")
        .h(BAR_HEIGHT)
        .min_w_0()
        .overflow_x_scroll()
        .px_2()
        .child(
            h_flex()
                .flex_none()
                .rounded_full()
                .bg(muted)
                .p(px(3.))
                .gap(px(2.))
                .children(tabs.into_iter().map(|(ix, name, shown)| {
                    tab(group, ix, name, cx)
                        .w(px(28.))
                        .h(px(24.))
                        .justify_center()
                        .rounded_full()
                        .text_color(muted_foreground)
                        .tooltip(move |window, cx| Tooltip::new(name).build(window, cx))
                        .child(Icon::new(icon(name)).size(px(14.)))
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
