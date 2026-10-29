//! Tests for the side docks' tabs (docs/behavior/g0-shape.md).

use gpui::TestAppContext;
use gpui_kit::component::dock::DockPlacement;
use gpui_kit::{Modifiers, MouseButton, VisualTestContext, point, px};
use serde_json::json;

use super::default_layout::{CHAT, FILES, PROBLEMS, SEARCH, SESSIONS, TERMINAL};
use super::frame_tests::{dump, open};

fn tab(name: &str, cx: &mut VisualTestContext) -> Option<gpui_kit::Bounds<gpui_kit::Pixels>> {
    // The test window's lookup takes a static name.
    cx.debug_bounds(format!("side-tab-{name}").leak())
}

// Pins: g0-shape#12
#[gpui::test]
fn the_sessions_column_has_the_old_text_chips(cx: &mut TestAppContext) {
    let (_, _, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    for name in [SESSIONS, FILES, SEARCH] {
        let chip = tab(name, cx).unwrap_or_else(|| panic!("{name} has a chip"));
        assert_eq!(chip.size.height, px(30.), "{name}'s chip is 30 px high");
    }
}

// Pins: g0-shape#12
#[gpui::test]
fn a_click_on_a_side_tab_shows_its_pane(cx: &mut TestAppContext) {
    let (frame, writes, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let files = tab(FILES, cx).expect("Files has a chip");
    cx.simulate_click(files.center(), Modifiers::none());
    cx.run_until_parked();
    assert_eq!(writes.borrow().len(), 1, "the click saves the layout");
    let layout = dump(&frame, cx);
    assert_eq!(
        layout["left_dock"]["panel"]["info"]["tabs"]["active_index"],
        json!(1),
        "{}",
        layout["left_dock"]["panel"]
    );
}

// Pins: g0-shape#12
#[gpui::test]
fn chat_and_the_bottom_dock_keep_the_kits_tabs(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    cx.update(|window, cx| {
        frame.read(cx).dock().clone().update(cx, |dock, cx| {
            dock.toggle_dock(DockPlacement::Bottom, window, cx);
        })
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(tab(SESSIONS, cx).is_some());
    assert!(
        cx.debug_bounds("dock-tab-0").is_some(),
        "the bottom dock draws the kit's tabs"
    );
    for name in [CHAT, PROBLEMS, TERMINAL] {
        assert!(tab(name, cx).is_none(), "{name} keeps the kit's tab");
    }
}

// Pins: g0-shape#2
#[gpui::test]
fn a_side_tab_drags_its_pane_elsewhere(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let files = tab(FILES, cx).expect("Files has a chip").center();
    let chat = point(px(800.), px(400.));
    cx.simulate_mouse_down(files, MouseButton::Left, Modifiers::none());
    for to in [point(files.x + px(20.), files.y), chat, chat] {
        cx.simulate_mouse_move(to, MouseButton::Left, Modifiers::none());
    }
    cx.simulate_mouse_up(chat, MouseButton::Left, Modifiers::none());
    cx.run_until_parked();
    let left = dump(&frame, cx)["left_dock"]["panel"]["children"].clone();
    let names: Vec<_> = left
        .as_array()
        .unwrap()
        .iter()
        .map(|pane| pane["panel_name"].clone())
        .collect();
    assert_eq!(
        names,
        [json!(SESSIONS), json!(SEARCH)],
        "Files left the column"
    );
}

// Pins: g0-shape#2
#[gpui::test]
fn a_pane_dropped_after_the_last_side_tab_joins_the_row(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    cx.update(|window, cx| {
        frame.read(cx).dock().clone().update(cx, |dock, cx| {
            dock.toggle_dock(DockPlacement::Bottom, window, cx);
        })
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let problems = cx
        .debug_bounds("dock-tab-0")
        .expect("the bottom dock's first tab")
        .center();
    let search = tab(SEARCH, cx).expect("Search has a chip");
    let after = point(search.right() + px(20.), search.center().y);
    cx.simulate_mouse_down(problems, MouseButton::Left, Modifiers::none());
    for to in [point(problems.x + px(20.), problems.y), after, after] {
        cx.simulate_mouse_move(to, MouseButton::Left, Modifiers::none());
    }
    cx.simulate_mouse_up(after, MouseButton::Left, Modifiers::none());
    cx.run_until_parked();
    let left = dump(&frame, cx)["left_dock"]["panel"]["children"].clone();
    let names: Vec<_> = left
        .as_array()
        .unwrap()
        .iter()
        .map(|pane| pane["panel_name"].clone())
        .collect();
    assert_eq!(
        names,
        [
            json!(SESSIONS),
            json!(FILES),
            json!(SEARCH),
            json!(PROBLEMS)
        ]
    );
}
