//! Tests for the docks' tabs (docs/behavior/g0-shape.md).

use gpui::TestAppContext;
use gpui_kit::component::dock::DockPlacement;
use gpui_kit::{Modifiers, MouseButton, VisualTestContext, point, px};
use serde_json::json;

use super::default_layout::{
    AGENTS, CHAT, CONTEXT, FILES, HISTORY, PROBLEMS, RUN, SEARCH, SESSIONS, SOURCE_CONTROL, TASKS,
    TERMINAL, WORKTREES,
};
use super::frame_tests::{dump, open};

fn tab(name: &str, cx: &mut VisualTestContext) -> Option<gpui_kit::Bounds<gpui_kit::Pixels>> {
    // The test window's lookup takes a static name.
    cx.debug_bounds(format!("tab-pill-{name}").leak())
}

// Pins: g0-shape#12
#[gpui::test]
fn the_sessions_column_has_the_drawers_pills_with_names(cx: &mut TestAppContext) {
    let (_, _, cx) = open(None, cx);
    cx.update(|window, cx| window.draw(cx).clear(cx));
    for name in [SESSIONS, FILES, SEARCH] {
        let pill = tab(name, cx).unwrap_or_else(|| panic!("{name} has a pill"));
        assert_eq!(
            pill.size.height,
            px(24.),
            "{name}'s pill is the drawer's height"
        );
        assert!(pill.size.width > px(28.), "{name}'s pill holds its name");
    }
}

// Pins: g0-shape#12
#[gpui::test]
fn the_drawer_has_the_old_icon_pills(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    cx.update(|window, cx| {
        frame.read(cx).dock().clone().update(cx, |dock, cx| {
            dock.toggle_dock(DockPlacement::Right, window, cx);
        })
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    for name in [
        SOURCE_CONTROL,
        AGENTS,
        TASKS,
        WORKTREES,
        RUN,
        CONTEXT,
        HISTORY,
    ] {
        let pill = tab(name, cx).unwrap_or_else(|| panic!("{name} has a pill"));
        assert_eq!(
            (pill.size.width, pill.size.height),
            (px(28.), px(24.)),
            "{name}'s pill"
        );
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
fn the_bottom_dock_has_the_same_pills_and_opens_from_them(cx: &mut TestAppContext) {
    let (frame, _, cx) = open(None, cx);
    let bottom_open = |cx: &mut VisualTestContext| {
        cx.update(|_, cx| {
            frame
                .read(cx)
                .dock()
                .read(cx)
                .is_dock_open(DockPlacement::Bottom)
        })
    };
    cx.update(|window, cx| window.draw(cx).clear(cx));
    assert!(!bottom_open(cx), "the bottom dock starts closed");
    assert!(tab(PROBLEMS, cx).is_some());
    let terminal = tab(TERMINAL, cx).expect("Terminal has a pill");
    assert_eq!(terminal.size.height, px(24.));
    assert!(tab(CHAT, cx).is_none(), "Chat keeps the kit's title");

    cx.simulate_click(terminal.center(), Modifiers::none());
    cx.run_until_parked();
    assert!(bottom_open(cx), "a pill on the closed strip opens it");
    let layout = dump(&frame, cx);
    assert_eq!(
        layout["bottom_dock"]["panel"]["info"]["tabs"]["active_index"],
        json!(1),
        "{}",
        layout["bottom_dock"]["panel"]
    );

    cx.update(|window, cx| window.draw(cx).clear(cx));
    let toggle = cx
        .debug_bounds("bottom-dock-toggle")
        .expect("the bottom dock's button");
    cx.simulate_click(toggle.center(), Modifiers::none());
    cx.run_until_parked();
    assert!(!bottom_open(cx), "the button closes it again");
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
    let problems = tab(PROBLEMS, cx).expect("Problems has a pill").center();
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
