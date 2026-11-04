//! The drawer's chrome toggle and in-drawer tool buttons.

use gpui::TestAppContext;
use gpui_kit::Modifiers;
use gpui_kit::component::dock::DockPlacement;
use serde_json::json;

use super::default_layout::{AGENTS, CONTEXT, RUN, SOURCE_CONTROL, TASKS, WORKTREES};
use super::drawer_buttons::{Press, extra_after, press};
use super::frame_tests::{dump, open};

// Pins: g0-shape#12
#[test]
fn pressing_a_pane_while_the_drawer_is_shut_opens_it_on_that_pane() {
    assert_eq!(press(false), Press::Open);
}

// The old drawer: tabs are not closable; only the drawer closes.
#[test]
fn pressing_a_pane_while_the_drawer_is_open_only_shows_it() {
    assert_eq!(press(true), Press::Show);
}

// The old drawer: the one ⋯-menu tab last picked stays, and the next pick replaces it.
#[test]
fn the_capsule_keeps_the_last_picked_overflow_tool_until_the_next_pick() {
    assert_eq!(extra_after(None, Some(AGENTS)), None);
    assert_eq!(extra_after(None, Some(RUN)), Some(RUN));
    assert_eq!(
        extra_after(Some(RUN), Some(TASKS)),
        Some(RUN),
        "a permanent tool leaves it there"
    );
    assert_eq!(extra_after(Some(RUN), None), Some(RUN));
    assert_eq!(
        extra_after(Some(RUN), Some(CONTEXT)),
        Some(CONTEXT),
        "the next pick replaces it"
    );
}

// Pins: g0-shape#12
#[gpui::test]
fn a_click_on_a_drawer_button_opens_the_drawer_on_that_pane(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let toggle = cx
        .debug_bounds("drawer-toggle")
        .expect("the drawer has a chrome toggle");
    cx.simulate_click(toggle.center(), Modifiers::none());
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    for name in [AGENTS, TASKS, SOURCE_CONTROL, SOURCE_CONTROL] {
        cx.update(|window, cx| window.draw(cx).clear(cx));
        let button = cx
            .debug_bounds(format!("drawer-button-{name}").leak())
            .unwrap_or_else(|| panic!("{name} has a directly visible drawer button"));
        cx.simulate_click(button.center(), Modifiers::none());
        cx.run_until_parked();
        let open = cx.update(|_, cx| {
            frame
                .read(cx)
                .dock()
                .read(cx)
                .is_dock_open(DockPlacement::Right)
        });
        assert!(open, "the drawer stays open when selecting {name}");
        let drawer = dump(&frame, cx)["right_dock"]["panel"].clone();
        let shown = drawer["info"]["tabs"]["active_index"]
            .as_u64()
            .expect("the drawer's tabs") as usize;
        assert_eq!(
            drawer["children"][shown]["panel_name"],
            json!(name),
            "{drawer}"
        );
    }
}

// The old drawer: four permanent tools (Files lives in the left column here), then the last pick.
#[gpui::test]
fn only_the_permanent_tools_and_the_last_pick_are_in_the_capsule(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let toggle = cx
        .debug_bounds("drawer-toggle")
        .expect("the drawer has a chrome toggle");
    cx.simulate_click(toggle.center(), Modifiers::none());
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let has = |cx: &mut gpui_kit::VisualTestContext, name: &str| {
        cx.debug_bounds(format!("drawer-button-{name}").leak())
            .is_some()
    };
    assert!(has(cx, SOURCE_CONTROL) && has(cx, AGENTS) && has(cx, TASKS));
    for name in [WORKTREES, RUN, CONTEXT] {
        assert!(!has(cx, name), "{name} waits in the menu until it's picked");
    }
    let pick = |cx: &mut gpui_kit::VisualTestContext, name: &str| {
        let id = cx.update(|_, cx| {
            let area = frame.read(cx).dock().read(cx);
            let mut found = None;
            area.layout(DockPlacement::Right)
                .expect("the drawer")
                .root()
                .walk(&mut |node| {
                    if let gpui_kit::component::dock::PaneRef::Tabs { panels, .. } = node.kind() {
                        for id in panels.iter() {
                            if area
                                .panel(*id)
                                .is_some_and(|panel| panel.panel_name(cx) == name)
                            {
                                found = Some(*id);
                            }
                        }
                    }
                });
            found.expect("the pane is in the drawer")
        });
        frame.update_in(cx, |frame, window, cx| {
            frame
                .dock()
                .update(cx, |dock, cx| dock.select_panel(id, window, cx))
        });
        cx.run_until_parked();
        cx.update(|window, cx| window.draw(cx).clear(cx));
    };
    pick(cx, RUN);
    assert!(has(cx, RUN), "the pick joins the capsule");
    pick(cx, TASKS);
    assert!(has(cx, RUN), "showing a permanent tool leaves it there");
    pick(cx, CONTEXT);
    assert!(
        has(cx, CONTEXT) && !has(cx, RUN),
        "the next pick replaces it"
    );
}
