//! The drawer's chrome toggle and in-drawer tool buttons.

use gpui::TestAppContext;
use gpui_kit::Modifiers;
use gpui_kit::component::dock::DockPlacement;
use serde_json::json;

use super::default_layout::{AGENTS, CONTEXT, RUN, SOURCE_CONTROL, TASKS, WORKTREES};
use super::drawer_buttons::{Press, press};
use super::frame_tests::{dump, open};

// Pins: g0-shape#12
#[test]
fn pressing_a_pane_while_the_drawer_is_shut_opens_it_on_that_pane() {
    assert_eq!(press(false, false), Press::Open);
    assert_eq!(press(false, true), Press::Open);
}

// Pins: g0-shape#12
#[test]
fn pressing_the_shown_pane_closes_the_drawer() {
    assert_eq!(press(true, true), Press::Close);
}

// Pins: g0-shape#12
#[test]
fn pressing_another_pane_while_the_drawer_is_open_only_shows_it() {
    assert_eq!(press(true, false), Press::Show);
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
    for name in [AGENTS, TASKS, WORKTREES, RUN, CONTEXT, SOURCE_CONTROL] {
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
